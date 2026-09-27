package relay

import (
	"context"
	"crypto/tls"
	"fmt"
	"io"
	"log/slog"
	"testing"
	"time"

	"github.com/quic-go/quic-go"

	"desklink/vps/internal/proto"
)

// quicClientSession 是 QUIC 路径的测试客户端：一条连接 + 一条双向流。
type quicClientSession struct {
	conn   *quic.Conn
	stream *quic.Stream
}

func (q *quicClientSession) writeCtl(t *testing.T, kind proto.RelayKind, payload []byte) {
	t.Helper()
	frame, err := proto.EncodeRelayCtl(kind, payload)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := q.stream.Write(frame); err != nil {
		t.Fatalf("quic write ctl: %v", err)
	}
}

func (q *quicClientSession) readCtl(t *testing.T) (*proto.RelayCtl, error) {
	t.Helper()
	// 先精确读 3 字节帧头（kind + len），再按声明的长度读 payload。
	// 这样每次 Read 都能重新设置 deadline，避免把上一次的过期 deadline 带进来。
	_ = q.stream.SetReadDeadline(time.Now().Add(5 * time.Second))
	hdr := make([]byte, proto.RelayCtlFrameSize)
	if _, err := io.ReadFull(q.stream, hdr); err != nil {
		return nil, err
	}
	plen := int(hdr[1])<<8 | int(hdr[2])
	if plen > proto.RelayCtlMaxPayload {
		return nil, fmt.Errorf("ctl payload too large: %d", plen)
	}
	payload := make([]byte, plen)
	if plen > 0 {
		if _, err := io.ReadFull(q.stream, payload); err != nil {
			return nil, err
		}
	}
	return &proto.RelayCtl{Kind: proto.RelayKind(hdr[0]), Payload: payload}, nil
}

// dialQUIC 以 QUIC 拨入 relay（ALPN 与 C# QuicTransport 一致）。
func dialQUIC(t *testing.T, addr string) *quicClientSession {
	t.Helper()
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	conn, err := quic.DialAddr(ctx, addr, &tls.Config{
		InsecureSkipVerify: true,
		ServerName:         "localhost",
		NextProtos:         []string{alpnRelay},
		MinVersion:         tls.VersionTLS12,
	}, &quic.Config{MaxIdleTimeout: 30 * time.Second})
	if err != nil {
		t.Fatalf("quic dial: %v", err)
	}
	str, err := conn.OpenStreamSync(ctx)
	if err != nil {
		_ = conn.CloseWithError(0, "")
		t.Fatalf("quic open stream: %v", err)
	}
	return &quicClientSession{conn: conn, stream: str}
}

// TestRelayQUICEndToEnd 验证 QUIC 路径：两台设备经 relay 接线后密文双向互通。
//
// 这是 P3 验收"Go 测试客户端打通两传输"的 QUIC 半边；TCP 半边由
// TestRelayTCPEndToEnd 覆盖。
func TestRelayQUICEndToEnd(t *testing.T) {
	token := "test-token-0123456789abcdef"
	reg := newFakeRegistry(t, token)
	pkA, privA := genKeyPair(t)
	pkB, privB := genKeyPair(t)
	idA := reg.addDevice(pkA, false)
	idB := reg.addDevice(pkB, false)
	reg.addPair(idA, idB)

	_, addr := startTestServer(t, reg, token)

	a := dialQUIC(t, addr)
	b := dialQUIC(t, addr)
	defer func() { _ = a.conn.CloseWithError(0, "") }()
	defer func() { _ = b.conn.CloseWithError(0, "") }()

	a.writeCtl(t, proto.KindRelayHello, mustDecodeHelloPayload(t, buildHello(t, pkA, privA, nil)))
	b.writeCtl(t, proto.KindRelayHello, mustDecodeHelloPayload(t, buildHello(t, pkB, privB, nil)))

	// 各读 HelloAck
	ackA := mustQUICCtl(t, a)
	if ackA.Kind != proto.KindRelayHelloAck {
		t.Fatalf("A expected HelloAck got 0x%02x", byte(ackA.Kind))
	}
	if ok, r, _ := proto.DecodeRelayStatus(ackA.Payload); !ok {
		t.Fatalf("A hello rejected: %s", r)
	}
	ackB := mustQUICCtl(t, b)
	if ackB.Kind != proto.KindRelayHelloAck {
		t.Fatalf("B expected HelloAck got 0x%02x", byte(ackB.Kind))
	}
	if ok, r, _ := proto.DecodeRelayStatus(ackB.Payload); !ok {
		t.Fatalf("B hello rejected: %s", r)
	}

	a.writeCtl(t, proto.KindRelayDial, proto.EncodeRelayDial(pkToDevice(pkB)))
	b.writeCtl(t, proto.KindRelayDial, proto.EncodeRelayDial(pkToDevice(pkA)))
	if !anyQUICDialOK(t, a) {
		t.Fatal("A did not get ok dial result")
	}
	if !anyQUICDialOK(t, b) {
		t.Fatal("B did not get ok dial result")
	}

	// QUIC 路径业务密文互通
	payload := []byte("quic-ciphertext-from-A")
	if _, err := a.stream.Write(payload); err != nil {
		t.Fatalf("A stream write: %v", err)
	}
	got := readNQUIC(t, b, len(payload))
	if string(got) != string(payload) {
		t.Fatalf("B got %q want %q", got, payload)
	}

	back := []byte("quic-ciphertext-from-B")
	if _, err := b.stream.Write(back); err != nil {
		t.Fatalf("B stream write: %v", err)
	}
	got2 := readNQUIC(t, a, len(back))
	if string(got2) != string(back) {
		t.Fatalf("A got %q want %q", got2, back)
	}
}

func mustQUICCtl(t *testing.T, q *quicClientSession) *proto.RelayCtl {
	t.Helper()
	frame, err := q.readCtl(t)
	if err != nil {
		t.Fatalf("quic read ctl: %v", err)
	}
	return frame
}

func waitQUICDialResult(t *testing.T, q *quicClientSession) (bool, string) {
	t.Helper()
	for i := 0; i < 20; i++ {
		frame := mustQUICCtl(t, q)
		if frame.Kind == proto.KindRelayDialResult {
			ok, reason, err := proto.DecodeRelayStatus(frame.Payload)
			if err != nil {
				t.Fatal(err)
			}
			return ok, reason
		}
	}
	t.Fatal("no quic dial result")
	return false, ""
}

// anyQUICDialOK 读帧直到 DialResult(ok)，跳过中间的 PeerEvent（对端 ready 通知）。
func anyQUICDialOK(t *testing.T, q *quicClientSession) bool {
	t.Helper()
	ok, _ := waitQUICDialResult(t, q)
	return ok
}

func readNQUIC(t *testing.T, q *quicClientSession, n int) []byte {
	t.Helper()
	_ = q.stream.SetReadDeadline(time.Now().Add(5 * time.Second))
	buf := make([]byte, n)
	got := 0
	for got < n {
		m, err := q.stream.Read(buf[got:])
		got += m
		if err != nil {
			t.Fatalf("quic read: %v (got %d/%d)", err, got, n)
		}
	}
	return buf
}

var _ = io.Discard
var _ = slog.LevelInfo
