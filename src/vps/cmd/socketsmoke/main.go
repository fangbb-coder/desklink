// Command socketsmoke 是 P2 验收用 unix socket 订阅客户端。
//
// 用法：
//
//	socketsmoke --socket /path/to/regnotify.sock --timeout 5s
//
// 从 socket 读 length-prefixed JSON 帧并打印到 stdout；超时自动退出。
package main

import (
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"log"
	"net"
	"os"
	"time"
)

func main() {
	socketPath := flag.String("socket", "", "unix socket path")
	timeout := flag.Duration("timeout", 5*time.Second, "dial + read timeout")
	flag.Parse()
	if *socketPath == "" {
		log.Fatal("--socket is required")
	}

	conn, err := net.DialTimeout("unix", *socketPath, *timeout)
	if err != nil {
		log.Fatalf("dial: %v", err)
	}
	defer conn.Close()

	conn.SetReadDeadline(time.Now().Add(*timeout))

	hdr := make([]byte, 4)
	for {
		if _, err := io.ReadFull(conn, hdr); err != nil {
			if err == io.EOF {
				return
			}
			if ne, ok := err.(net.Error); ok && ne.Timeout() {
				log.Printf("read timeout reached (%v), exiting", *timeout)
				return
			}
			log.Fatalf("read frame header: %v", err)
		}
		length := uint32(hdr[0])<<24 | uint32(hdr[1])<<16 | uint32(hdr[2])<<8 | uint32(hdr[3])
		if length > 64*1024 {
			log.Fatalf("frame too large: %d", length)
		}
		body := make([]byte, length)
		if _, err := io.ReadFull(conn, body); err != nil {
			log.Fatalf("read frame body: %v", err)
		}
		// 立即回 ACK 让 publisher 删除 pending
		ack := []byte{0, 0, 0, 0} // 简化：ACK 由 subscriber 真实实现发；这里只读
		_ = ack
		var pretty map[string]interface{}
		_ = json.Unmarshal(body, &pretty)
		// 输出：frame length + raw + parsed
		fmt.Fprintf(os.Stdout, "FRAME length=%d body=%s\n", length, string(body))
		if pretty != nil {
			fmt.Fprintf(os.Stdout, "PARSED kind=%v event_id=%v\n", pretty["kind"], pretty["event_id"])
		}
		// 短超时让 socket smoke 能在合理时间内退出
		conn.SetReadDeadline(time.Now().Add(500 * time.Millisecond))
	}
}
