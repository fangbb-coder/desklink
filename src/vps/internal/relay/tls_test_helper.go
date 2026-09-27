package relay

import (
	"crypto/tls"
	"net"
)

// tlsClient 把已拨通的裸 TCP 连接升级为 TLS 客户端（测试用，跳过证书校验）。
//
// 生产路径由 C# TcpTlsTransport 做正式 TLS；测试只关心"字节流能否建立 + 数据面"，
// 因此显式 InsecureSkipVerify（等价于 C# 侧 P4 阶段"接受所有证书"的占位策略）。
func tlsClient(raw net.Conn) *tls.Conn {
	return tls.Client(raw, &tls.Config{
		InsecureSkipVerify: true,
		ServerName:         "localhost",
		MinVersion:         tls.VersionTLS12,
		NextProtos:         []string{alpnRelay},
	})
}
