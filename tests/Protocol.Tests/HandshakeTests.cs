using DeskLink.Protocol.Common;
using DeskLink.Protocol.Handshake;

namespace Protocol.Tests;

public class HandshakeTests
{
    [Fact]
    public void Handshake_Produces_Matching_SessionKeys()
    {
        var alice = HandshakeKeys.Generate();
        var bob = HandshakeKeys.Generate();

        var aliceHint = new byte[HandshakeMessages.DeviceIdHintLen];
        var bobHint = new byte[HandshakeMessages.DeviceIdHintLen];
        System.Security.Cryptography.RandomNumberGenerator.Fill(aliceHint);
        System.Security.Cryptography.RandomNumberGenerator.Fill(bobHint);

        var initiator = new HandshakeSession.Initiator(alice, aliceHint, ProtocolConstants.PathKind.Relay);
        var responder = new HandshakeSession.Responder(bob, bobHint);

        var initBytes = initiator.BuildInitiatorHello();
        var respBytes = responder.HandleInitiatorHello(initBytes);
        var initFinish = initiator.HandleResponderHello(respBytes);
        var respFinish = responder.HandleInitiatorFinish(initFinish);

        Assert.NotNull(initiator.SessionKeys);
        Assert.NotNull(responder.SessionKeys);

        var (aliceSend, aliceRecv) = initiator.SessionKeys!.Value;
        var (bobSend, bobRecv) = responder.SessionKeys!.Value;

        // 发起方的 send == 接收方的 recv
        Assert.Equal(aliceSend, bobRecv);
        // 发起方的 recv == 接收方的 send
        Assert.Equal(aliceRecv, bobSend);

        Assert.True(HandshakeMessages.DecodeResponderFinish(respFinish).ConfirmOk);
    }

    [Fact]
    public void Handshake_TamperedResponderHello_FailsSignature()
    {
        var alice = HandshakeKeys.Generate();
        var bob = HandshakeKeys.Generate();

        var hint = new byte[HandshakeMessages.DeviceIdHintLen];
        System.Security.Cryptography.RandomNumberGenerator.Fill(hint);

        var initiator = new HandshakeSession.Initiator(alice, hint, ProtocolConstants.PathKind.Relay);
        var responder = new HandshakeSession.Responder(bob, hint);

        var initBytes = initiator.BuildInitiatorHello();
        var respBytes = responder.HandleInitiatorHello(initBytes);

        // 篡改 respBytes 最后一字节（签名位）
        respBytes[respBytes.Length - 1] ^= 0xFF;

        Assert.Throws<HandshakeException>(() => initiator.HandleResponderHello(respBytes));
    }

    [Fact]
    public void Handshake_DirectPath_ProducesMatchingKeys()
    {
        var alice = HandshakeKeys.Generate();
        var bob = HandshakeKeys.Generate();
        var hint = new byte[HandshakeMessages.DeviceIdHintLen];

        var initiator = new HandshakeSession.Initiator(alice, hint, ProtocolConstants.PathKind.Direct);
        var responder = new HandshakeSession.Responder(bob, hint);

        var initBytes = initiator.BuildInitiatorHello();
        var respBytes = responder.HandleInitiatorHello(initBytes);
        initiator.HandleResponderHello(respBytes);

        var (aliceSend, aliceRecv) = initiator.SessionKeys!.Value;
        var (bobSend, bobRecv) = responder.SessionKeys!.Value;
        Assert.Equal(aliceSend, bobRecv);
        Assert.Equal(aliceRecv, bobSend);
    }
}
