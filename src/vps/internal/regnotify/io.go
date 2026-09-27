package regnotify

import (
	"errors"
	"fmt"
	"io"
	"net"

	"desklink/vps/internal/proto"
)

// readSocketFrame 从 r 读一帧 socket 推送（length-prefixed JSON）。
// 帧头 4B 大端长度 + N 字节 body；上限 proto.SocketFrameMaxBytes。
func readSocketFrame(r io.Reader) ([]byte, error) {
	hdr := make([]byte, 4)
	if _, err := io.ReadFull(r, hdr); err != nil {
		return nil, err
	}
	length := uint32(hdr[0])<<24 | uint32(hdr[1])<<16 | uint32(hdr[2])<<8 | uint32(hdr[3])
	if length > proto.SocketFrameMaxBytes {
		return nil, fmt.Errorf("regnotify: declared length %d exceeds SocketFrameMaxBytes: %w", length, proto.ErrFrameTooLarge)
	}
	body := make([]byte, length)
	if _, err := io.ReadFull(r, body); err != nil {
		return nil, err
	}
	return body, nil
}

// writeSocketFrame 写一帧 socket 推送。
func writeSocketFrame(w io.Writer, body []byte) error {
	frame, err := proto.EncodeSocketFrame(body)
	if err != nil {
		return fmt.Errorf("regnotify: encode frame: %w", err)
	}
	_, err = w.Write(frame)
	return err
}

func isClosedErr(err error) bool {
	if err == nil {
		return false
	}
	s := err.Error()
	return errors.Is(err, net.ErrClosed) ||
		s == "use of closed network connection" ||
		s == "EOF"
}

func isTimeout(err error) bool {
	var ne net.Error
	return errors.As(err, &ne) && ne.Timeout()
}
