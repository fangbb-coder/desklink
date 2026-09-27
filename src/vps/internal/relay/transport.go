// Package relay 子文件 transport：把 QUIC 流与 TLS 连接统一成 relay.Conn。
//
// 两条传输的语义差异：
//   - QUIC：一条连接上取第一条双向流作为会话字节流。Send/Recv 直接映射
//     到 stream 的 Write/Read。
//   - TCP/TLS：SslStream 的 Write/Read。
//
// 两者都只提供"有序字节流"语义；relay 不依赖任何消息边界（控制帧自带长度前缀）。
package relay

import (
	"context"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/tls"
	"crypto/x509"
	"crypto/x509/pkix"
	"errors"
	"math/big"
	"net"
	"time"

	"github.com/quic-go/quic-go"
)

// streamConn 是 TLS / 任意 net.Conn 的 Conn 适配。
type streamConn struct {
	c    net.Conn
	kind string
}

func newStreamConn(c net.Conn, kind string) *streamConn {
	return &streamConn{c: c, kind: kind}
}

// Recv 读一段字节，并让 ctx 取消能**立刻**唤醒阻塞中的 Read。
//
// 为什么需要 context.AfterFunc：net.Conn 没有原生 context 支持。只设置
// SetReadDeadline(ctx.Deadline()) 只能覆盖"deadline 到期"，覆盖不了"ctx 被
// cancel"（那种情况要一直等到 45s 读超时才醒）。这会让服务关闭时留下几十秒的
// 收尾延迟——relayd 的 wg.Wait() 就卡在这上面。
func (s *streamConn) Recv(ctx context.Context, buf []byte) (int, error) {
	stop := context.AfterFunc(ctx, func() {
		_ = s.c.SetReadDeadline(time.Now())
	})
	defer stop()

	if dl, ok := ctx.Deadline(); ok {
		_ = s.c.SetReadDeadline(dl)
	} else {
		_ = s.c.SetReadDeadline(time.Now().Add(45 * time.Second))
	}
	n, err := s.c.Read(buf)
	if isTimeoutErr(err) {
		select {
		case <-ctx.Done():
			return n, ctx.Err()
		default:
			// 纯空闲超时：当作"读到 0 字节、无错误"交回上层。
			// 上层（readCtl / Pump）会继续循环，由它们自己的策略决定何时放弃。
			return n, nil
		}
	}
	return n, err
}

func (s *streamConn) Send(ctx context.Context, data []byte) error {
	_ = s.c.SetWriteDeadline(time.Now().Add(30 * time.Second))
	_, err := s.c.Write(data)
	return err
}

func (s *streamConn) Close() error {
	return s.c.Close()
}

func (s *streamConn) RemoteAddr() string {
	if a := s.c.RemoteAddr(); a != nil {
		return a.String()
	}
	return ""
}

func (s *streamConn) Kind() string { return s.kind }

// quicConn 是 QUIC 流的 Conn 适配。
type quicConn struct {
	conn   *quic.Conn
	stream *quic.Stream
}

func newQUICConn(conn *quic.Conn, stream *quic.Stream) *quicConn {
	return &quicConn{conn: conn, stream: stream}
}

func (q *quicConn) Send(ctx context.Context, data []byte) error {
	_ = q.stream.SetWriteDeadline(time.Now().Add(30 * time.Second))
	_, err := q.stream.Write(data)
	return err
}

// Recv 与 streamConn.Recv 语义保持一致：ctx 取消立刻唤醒阻塞中的 Read，
// ctx 带 deadline 时用该 deadline（早期实现无条件用 45s，忽略了 ctx.Deadline，
// 于是 readCtl 的超时在 QUIC 路径上不准）。
func (q *quicConn) Recv(ctx context.Context, buf []byte) (int, error) {
	stop := context.AfterFunc(ctx, func() {
		_ = q.stream.SetReadDeadline(time.Now())
	})
	defer stop()

	if dl, ok := ctx.Deadline(); ok {
		_ = q.stream.SetReadDeadline(dl)
	} else {
		_ = q.stream.SetReadDeadline(time.Now().Add(45 * time.Second))
	}
	n, err := q.stream.Read(buf)
	if isTimeoutErr(err) {
		select {
		case <-ctx.Done():
			return n, ctx.Err()
		default:
			return n, nil
		}
	}
	return n, err
}

func (q *quicConn) Close() error {
	_ = q.stream.Close()
	return q.conn.CloseWithError(0, "")
}

func (q *quicConn) RemoteAddr() string {
	if a := q.conn.RemoteAddr(); a != nil {
		return a.String()
	}
	return ""
}

func (q *quicConn) Kind() string { return "quic" }

func isTimeoutErr(err error) bool {
	var ne net.Error
	return errors.As(err, &ne) && ne.Timeout()
}

// selfSignedCert 生成一张内存自签证书（仅供 AllowInsecureTLS 测试路径）。
//
// 生产路径必须用 installer/vps/gen-cert.sh 生成的证书；自签会破坏 TOFU 前提，
// 因此本函数只允许在测试配置下被调用。
func selfSignedCert() (tls.Certificate, error) {
	priv, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		return tls.Certificate{}, err
	}
	serial, err := rand.Int(rand.Reader, new(big.Int).Lsh(big.NewInt(1), 128))
	if err != nil {
		return tls.Certificate{}, err
	}
	tmpl := &x509.Certificate{
		SerialNumber:          serial,
		Subject:               pkix.Name{CommonName: "desklink-relayd-test"},
		NotBefore:             time.Now().Add(-time.Hour),
		NotAfter:              time.Now().Add(24 * time.Hour),
		KeyUsage:              x509.KeyUsageDigitalSignature | x509.KeyUsageCertSign,
		ExtKeyUsage:           []x509.ExtKeyUsage{x509.ExtKeyUsageServerAuth},
		DNSNames:              []string{"localhost"},
		IPAddresses:           []net.IP{net.ParseIP("127.0.0.1"), net.ParseIP("::1")},
		IsCA:                  true,
		BasicConstraintsValid: true,
	}
	der, err := x509.CreateCertificate(rand.Reader, tmpl, tmpl, &priv.PublicKey, priv)
	if err != nil {
		return tls.Certificate{}, err
	}
	return tls.Certificate{
		Certificate: [][]byte{der},
		PrivateKey:  priv,
		Leaf:        tmpl,
	}, nil
}
