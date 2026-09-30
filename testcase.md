# DeskLink 测试用例清单与执行结果

> **生成时间**：2026-09-30 22:10　**代码版本**：`bb4b314`　**执行方式**：`dotnet test`（6 个项目全量）
>
> 本文件由 `trx` 结果文件**自动导出**，不是手写维护的清单——
> 跑一遍测试就重新生成一遍，避免文档和实际跑的东西对不上。

```
总计 728 个用例：通过 728，失败 0
```

## 汇总

| 测试项目 | 用例数 | 通过 | 失败 | 覆盖范围 |
|---|---:|---:|---:|---|
| `Protocol.Tests` | 71 | 71 | 0 | 二进制帧编解码、大小端、JSON 契约、Blake3 / X25519 / HKDF / AEAD、Go 端对齐锚点 |
| `Service.Tests` | 209 | 209 | 0 | 命名管道 RPC、配置解析与持久化、文件传输（含 E2E 取消）、中继会话、媒体通道、直连拨号、键盘注入 |
| `Client.Tests` | 141 | 141 | 0 | ViewModel 业务判断、UI 诚实性、文件进度轮询、安全（DPAPI / 签名） |
| `DirectHandshake.Tests` | 41 | 41 | 0 | Ed25519 + SIGMA 真实握手、设备 id 交换、断连与超时 |
| `Agent.Tests` | 137 | 137 | 0 | P7 桌面代理：输入注入、编码器选择、坐标与 DPI 换算、退避策略、帧收发 |
| `Panel.Tests` | 129 | 129 | 0 | 控制面板 ViewModel、WPF 渲染冒烟、静态护栏、CLI 参数拼装、Service 输出解析 |
| **合计** | **728** | **728** | **0** | |

## 逐条明细

> 按「测试类」分组，标题括号里是该类所在源文件。`[Theory]` 的参数列在最后。

### `Protocol.Tests`（71 个）

#### `Blake3Tests`（`Blake3Tests.cs`，19 个）

| # | 用例 | 参数 | 结果 |
|---:|---|---|---|
| 1 | `Hash_EmptyInput_ProducesOfficialVector` |  | ✅ |
| 2 | `Hash_Matches_Official` | (len: 0, expectedHex: "af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc"···, expectedKeyedHex: "92b2b75604ed3c761f9d6f62392c8a9227ad0ea3f09573e783"···) | ✅ |
| 3 | `Hash_Matches_Official` | (len: 1, expectedHex: "2d3adedff11b61f14c886e35afa036736dcd87a74d27b5c151"···, expectedKeyedHex: "6d7878dfff2f485635d39013278ae14f1454b8c0a3a2d34bc1"···) | ✅ |
| 4 | `Hash_Matches_Official` | (len: 1023, expectedHex: "10108970eeda3eb932baac1428c7a2163b0e924c9a9e25b35b"···, expectedKeyedHex: "c951ecdf03288d0fcc96ee3413563d8a6d3589547f2c2fb36d"···) | ✅ |
| 5 | `Hash_Matches_Official` | (len: 1024, expectedHex: "42214739f095a406f3fc83deb889744ac00df831c10daa5518"···, expectedKeyedHex: "75c46f6f3d9eb4f55ecaaee480db732e6c2105546f1e675003"···) | ✅ |
| 6 | `Hash_Matches_Official` | (len: 1025, expectedHex: "d00278ae47eb27b34faecf67b4fe263f82d5412916c1ffd97c"···, expectedKeyedHex: "357dc55de0c7e382c900fd6e320acc04146be01db6a8ce7210"···) | ✅ |
| 7 | `Hash_Matches_Official` | (len: 63, expectedHex: "e9bc37a594daad83be9470df7f7b3798297c3d834ce80ba85d"···, expectedKeyedHex: "bb1eb5d4afa793c1ebdd9fb08def6c36d10096986ae0cfe148"···) | ✅ |
| 8 | `Hash_Matches_Official` | (len: 64, expectedHex: "4eed7141ea4a5cd4b788606bd23f46e212af9cacebacdc7d1f"···, expectedKeyedHex: "ba8ced36f327700d213f120b1a207a3b8c04330528586f414d"···) | ✅ |
| 9 | `Hash_Matches_Official` | (len: 65, expectedHex: "de1e5fa0be70df6d2be8fffd0e99ceaa8eb6e8c93a63f2d8d1"···, expectedKeyedHex: "c0a4edefa2d2accb9277c371ac12fcdbb52988a86edc54f071"···) | ✅ |
| 10 | `Hash_Matches_Official` | (len: 8192, expectedHex: "aae792484c8efe4f19e2ca7d371d8c467ffb10748d8a5a1ae5"···, expectedKeyedHex: "dc9637c8845a770b4cbf76b8daec0eebf7dc2eac11498517f0"···) | ✅ |
| 11 | `KeyedHash_Matches_Official` | (len: 0, _: "af1349b9f5f9a1a6a0404dea36dcc9499bcb25c9adc112b7cc"···, expectedKeyedHex: "92b2b75604ed3c761f9d6f62392c8a9227ad0ea3f09573e783"···) | ✅ |
| 12 | `KeyedHash_Matches_Official` | (len: 1, _: "2d3adedff11b61f14c886e35afa036736dcd87a74d27b5c151"···, expectedKeyedHex: "6d7878dfff2f485635d39013278ae14f1454b8c0a3a2d34bc1"···) | ✅ |
| 13 | `KeyedHash_Matches_Official` | (len: 1023, _: "10108970eeda3eb932baac1428c7a2163b0e924c9a9e25b35b"···, expectedKeyedHex: "c951ecdf03288d0fcc96ee3413563d8a6d3589547f2c2fb36d"···) | ✅ |
| 14 | `KeyedHash_Matches_Official` | (len: 1024, _: "42214739f095a406f3fc83deb889744ac00df831c10daa5518"···, expectedKeyedHex: "75c46f6f3d9eb4f55ecaaee480db732e6c2105546f1e675003"···) | ✅ |
| 15 | `KeyedHash_Matches_Official` | (len: 1025, _: "d00278ae47eb27b34faecf67b4fe263f82d5412916c1ffd97c"···, expectedKeyedHex: "357dc55de0c7e382c900fd6e320acc04146be01db6a8ce7210"···) | ✅ |
| 16 | `KeyedHash_Matches_Official` | (len: 63, _: "e9bc37a594daad83be9470df7f7b3798297c3d834ce80ba85d"···, expectedKeyedHex: "bb1eb5d4afa793c1ebdd9fb08def6c36d10096986ae0cfe148"···) | ✅ |
| 17 | `KeyedHash_Matches_Official` | (len: 64, _: "4eed7141ea4a5cd4b788606bd23f46e212af9cacebacdc7d1f"···, expectedKeyedHex: "ba8ced36f327700d213f120b1a207a3b8c04330528586f414d"···) | ✅ |
| 18 | `KeyedHash_Matches_Official` | (len: 65, _: "de1e5fa0be70df6d2be8fffd0e99ceaa8eb6e8c93a63f2d8d1"···, expectedKeyedHex: "c0a4edefa2d2accb9277c371ac12fcdbb52988a86edc54f071"···) | ✅ |
| 19 | `KeyedHash_Matches_Official` | (len: 8192, _: "aae792484c8efe4f19e2ca7d371d8c467ffb10748d8a5a1ae5"···, expectedKeyedHex: "dc9637c8845a770b4cbf76b8daec0eebf7dc2eac11498517f0"···) | ✅ |

#### `E2EAlignmentTests`（`E2EAlignmentTests.cs`，19 个）

| # | 用例 | 结果 |
|---:|---|---|
| 20 | `AeadSession_Open_RejectsOutOfOrderCounter` | ✅ |
| 21 | `AeadSession_SealOpen_RoundTrip` | ✅ |
| 22 | `DeviceChallenge_Transcript_Matches_Go_Anchor` | ✅ |
| 23 | `FrameCodec_Hello_DeskLink_Matches_Go_Anchor` | ✅ |
| 24 | `FrameCodec_Oversize_Rejects_At_Boundary` | ✅ |
| 25 | `FrameCodec_Ping_EmptyPayload_Matches_Go_RoundTrip` | ✅ |
| 26 | `Handshake_DirectPath_SessionKeys_BitEqual_BothRoles` | ✅ |
| 27 | `Handshake_MessageSizes_Match_Protocol_Spec` | ✅ |
| 28 | `Handshake_RelayPath_SessionKeys_BitEqual_BothRoles` | ✅ |
| 29 | `Handshake_TamperedResponderHello_Rejected` | ✅ |
| 30 | `HkdfSha256_Derive_Matches_Rfc5869_Kat` | ✅ |
| 31 | `MuxTcp_InputKey_CAFE_Sid33_Matches_Go_RoundTrip` | ✅ |
| 32 | `RelayCtl_DialFrame_Matches_Go_Anchor` | ✅ |
| 33 | `RelayCtl_HelloFrame_Matches_Go_Anchor` | ✅ |
| 34 | `RelayCtl_StatusFrame_Matches_Go_Anchor` | ✅ |
| 35 | `StreamId_FromLogical_Allocations_Match_Go_Cases` | ✅ |
| 36 | `X25519_Ecdh_Rfc7748_KnownAnswer_INTEROP_RISK` | ✅ |
| 37 | `X25519_Ecdh_Self_Consistency_BitEqual` | ✅ |
| 38 | `X25519_Ecdh_TwoParties_BitEqual` | ✅ |

#### `FileAckFrameTests`（`FileAckFrameTests.cs`，14 个）

| # | 用例 | 参数 | 结果 |
|---:|---|---|---|
| 39 | `Decode_BitmapLengthMismatch_Throws` |  | ✅ |
| 40 | `Decode_PayloadTooShort_Throws` |  | ✅ |
| 41 | `IsAcked_AfterRange_IsFalse` |  | ✅ |
| 42 | `IsAcked_BeforeBaseChunk_IsFalse` |  | ✅ |
| 43 | `IsAcked_MsbFirst` |  | ✅ |
| 44 | `IsAcked_PartialLastByte` |  | ✅ |
| 45 | `PackBitmap_MatchesExpected` | (bits: [], expected: []) | ✅ |
| 46 | `PackBitmap_MatchesExpected` | (bits: [True, False, True, False], expected: [160]) | ✅ |
| 47 | `PackBitmap_MatchesExpected` | (bits: [True, True, True, True, True, ···], expected: [255]) | ✅ |
| 48 | `PackUnpack_RoundTrip` |  | ✅ |
| 49 | `RoundTrip_EncodeDecode` |  | ✅ |
| 50 | `RoundTrip_LargeBaseChunk` |  | ✅ |
| 51 | `UnpackBitmap_MatchesExpected` | (bitmap: [160], bitCount: 4, expected: [True, False, True, False]) | ✅ |
| 52 | `UnpackBitmap_MatchesExpected` | (bitmap: [255], bitCount: 8, expected: [True, True, True, True, True, ···]) | ✅ |

#### `FrameCodecTests`（`FrameCodecTests.cs`，10 个）

| # | 用例 | 结果 |
|---:|---|---|
| 53 | `Encode_Decode_AtWireLimit_RoundTrips` | ✅ |
| 54 | `Encode_Oversize_Throws` | ✅ |
| 55 | `Encode_PayloadOverUint16WireLimit_ThrowsInsteadOfTruncating` | ✅ |
| 56 | `EncodeDecode_RoundTrip` | ✅ |
| 57 | `MaxFramePayloadWireSize_Matches_Go_Anchor` | ✅ |
| 58 | `MuxTcp_RoundTrip` | ✅ |
| 59 | `MuxTcp_ShortBuffer_ReturnsZero` | ✅ |
| 60 | `StreamId_FromLogical_Range` | ✅ |
| 61 | `TryDecode_OversizeLength_Throws` | ✅ |
| 62 | `TryDecode_ShortBuffer_ReturnsZero` | ✅ |

#### `HandshakeTests`（`HandshakeTests.cs`，3 个）

| # | 用例 | 结果 |
|---:|---|---|
| 63 | `Handshake_DirectPath_ProducesMatchingKeys` | ✅ |
| 64 | `Handshake_Produces_Matching_SessionKeys` | ✅ |
| 65 | `Handshake_TamperedResponderHello_FailsSignature` | ✅ |

#### `HkdfAndAeadTests`（`HkdfAndAeadTests.cs`，4 个）

| # | 用例 | 结果 |
|---:|---|---|
| 66 | `Aead_DifferentKey_Fails` | ✅ |
| 67 | `Aead_ReorderedCounter_Fails` | ✅ |
| 68 | `Aead_RoundTrip_SequentialFrames` | ✅ |
| 69 | `Hkdf_Derive_Deterministic` | ✅ |

#### `X25519AlignmentTests`（`X25519AlignmentTests.cs`，2 个）

| # | 用例 | 结果 |
|---:|---|---|
| 70 | `X25519_BouncyCastle_Rfc7748_FirstVector_Quantify` | ✅ |
| 71 | `X25519_Rfc7748_AllOnesScalar_BasePoint_Iter1000` | ✅ |

### `Service.Tests`（209 个）

#### `AgentLauncherTests`（`AgentLauncherTests.cs`，10 个）

| # | 用例 | 结果 |
|---:|---|---|
| 1 | `BuildArguments_Always_Includes_Run` | ✅ |
| 2 | `BuildArguments_Includes_CaptureParameters` | ✅ |
| 3 | `BuildArguments_Includes_MediaPipe_And_NoInject` | ✅ |
| 4 | `BuildArguments_Inject_Mode_Adds_Inject` | ✅ |
| 5 | `BuildArguments_Never_Combines_Flag_And_Value` | ✅ |
| 6 | `BuildArguments_Omits_Default_CaptureParameters` | ✅ |
| 7 | `Double_Start_Returns_First` | ✅ |
| 8 | `Inject_With_NoInject_Allows_Stub_Start` | ✅ |
| 9 | `Inject_Without_NoInject_Hard_Fails` | ✅ |
| 10 | `No_Inject_Mode_Runs_In_Stub_When_Exe_Missing` | ✅ |

#### `BackoffPolicyTests`（`BackoffPolicyTests.cs`，4 个）

| # | 用例 | 结果 |
|---:|---|---|
| 11 | `Delay_Doubles_Until_Max` | ✅ |
| 12 | `First_Delay_Is_Approximately_Base` | ✅ |
| 13 | `Invalid_Base_Or_Max_Throws` | ✅ |
| 14 | `Reset_Restarts_From_Base` | ✅ |

#### `CommandLineParserTests`（`CommandLineParserTests.cs`，28 个）

| # | 用例 | 参数 | 结果 |
|---:|---|---|---|
| 15 | `Console_Mode_Flag` |  | ✅ |
| 16 | `Data_Dir_Overrides_Default_And_Derives_Instance` |  | ✅ |
| 17 | `Direct_Port_Range_Validated` |  | ✅ |
| 18 | `Enable_Direct_Appears_In_Help` |  | ✅ |
| 19 | `Enable_Direct_Flag_Defaults_Off_And_Turns_On` |  | ✅ |
| 20 | `FileScope_DefaultsToEmpty_WhichMeansDenyAll` |  | ✅ |
| 21 | `FileScope_ParsesRepeatableAndNormalisesToAbsolute` |  | ✅ |
| 22 | `Firewall_Repair_ParsesPort` |  | ✅ |
| 23 | `Firewall_Set_ParsesPortAndValue` | (value: "0", expected: False) | ✅ |
| 24 | `Firewall_Set_ParsesPortAndValue` | (value: "1", expected: True) | ✅ |
| 25 | `Firewall_Set_ParsesPortAndValue` | (value: "disable", expected: False) | ✅ |
| 26 | `Firewall_Set_ParsesPortAndValue` | (value: "enable", expected: True) | ✅ |
| 27 | `Firewall_Set_ParsesPortAndValue` | (value: "off", expected: False) | ✅ |
| 28 | `Firewall_Set_ParsesPortAndValue` | (value: "on", expected: True) | ✅ |
| 29 | `Firewall_Set_ParsesPortAndValue` | (value: "ON", expected: True) | ✅ |
| 30 | `Firewall_Set_RejectsInvalidPort` | (port: "0") | ✅ |
| 31 | `Firewall_Set_RejectsInvalidPort` | (port: "65536") | ✅ |
| 32 | `Firewall_Set_RejectsInvalidPort` | (port: "abc") | ✅ |
| 33 | `Firewall_Set_RejectsUnknownValue` | (value: "") | ✅ |
| 34 | `Firewall_Set_RejectsUnknownValue` | (value: "maybe") | ✅ |
| 35 | `Firewall_Set_RejectsUnknownValue` | (value: "yes") | ✅ |
| 36 | `Firewall_Set_RequiresBothArguments` |  | ✅ |
| 37 | `Help_Returns_Zero_Exit_And_No_Options_Apply` |  | ✅ |
| 38 | `Missing_Value_Returns_Error` |  | ✅ |
| 39 | `No_Args_Returns_Default_Options` |  | ✅ |
| 40 | `Pipe_Prefix_Override` |  | ✅ |
| 41 | `Relay_Url_Accepts_Absolute_Uri` |  | ✅ |
| 42 | `Unknown_Arg_Returns_Error` |  | ✅ |

#### `FileAckTrackerTests`（`FileAckTrackerTests.cs`，9 个）

| # | 用例 | 结果 |
|---:|---|---|
| 43 | `ConcurrentRecordAck_DoesNotCrash` | ✅ |
| 44 | `Init_ResetsState` | ✅ |
| 45 | `IsComplete_EmptyTotal` | ✅ |
| 46 | `IsComplete_TrueWhenAllAcked` | ✅ |
| 47 | `MissingChunks_RespectsMaxCount` | ✅ |
| 48 | `RecordAck_AdjacentRanges_Merge` | ✅ |
| 49 | `RecordAck_IgnoresOutOfRange` | ✅ |
| 50 | `RecordAck_MultipleRanges_Merge` | ✅ |
| 51 | `RecordAck_SingleRange` | ✅ |

#### `FileTransferTests`（`FileTransferTests.cs`，45 个）

| # | 用例 | 参数 | 结果 |
|---:|---|---|---|
| 52 | `ChunkLayout_ComputesChunkCount` | (total: 0, chunk: 1000, expected: 0) | ✅ |
| 53 | `ChunkLayout_ComputesChunkCount` | (total: 1000, chunk: 1000, expected: 1) | ✅ |
| 54 | `ChunkLayout_ComputesChunkCount` | (total: 1001, chunk: 1000, expected: 2) | ✅ |
| 55 | `ChunkLayout_ComputesChunkCount` | (total: 2500, chunk: 1000, expected: 3) | ✅ |
| 56 | `ChunkLayout_ComputesChunkCount` | (total: 999, chunk: 1000, expected: 1) | ✅ |
| 57 | `ChunkLayout_LastChunkIsShorter` |  | ✅ |
| 58 | `ChunkLayout_RejectsIllegalChunkSize` |  | ✅ |
| 59 | `Conflict_OverwriteUsesTarget` |  | ✅ |
| 60 | `Conflict_RenameAppendsCounter` |  | ✅ |
| 61 | `Conflict_RenameWhenFreeKeepsName` |  | ✅ |
| 62 | `Conflict_SkipReturnsSkippedWhenExists` |  | ✅ |
| 63 | `E2E_Cancel_SenderStopsAndReceiverDropsPart` |  | ✅ |
| 64 | `E2E_ConflictOverwrite_ReplacesTarget` |  | ✅ |
| 65 | `E2E_ConflictRename_KeepsExisting` |  | ✅ |
| 66 | `E2E_ConflictSkip_LeavesTargetUntouched` |  | ✅ |
| 67 | `E2E_DigestMismatch_FailsAndLeavesNoTarget` |  | ✅ |
| 68 | `E2E_DiskFull_FailsAndKeepsPartForResume` |  | ✅ |
| 69 | `E2E_Download_PullWorks` |  | ✅ |
| 70 | `E2E_EmptyFile_Transfers` |  | ✅ |
| 71 | `E2E_ListRemoteDirectory` |  | ✅ |
| 72 | `E2E_Resume_OnlyMissingChunksAreSent` |  | ✅ |
| 73 | `E2E_ScopeDenied_OnReceiver` |  | ✅ |
| 74 | `E2E_Upload_MultiChunk_TransfersAndVerifies` |  | ✅ |
| 75 | `E2E_UploadOutOfScopeSource_IsRejectedLocally` |  | ✅ |
| 76 | `Frames_AckWrapRoundTrip` |  | ✅ |
| 77 | `Frames_ChunkRejectsOversize` |  | ✅ |
| 78 | `Frames_ChunkRoundTrip` |  | ✅ |
| 79 | `Frames_CompleteAndStatusRoundTrip` |  | ✅ |
| 80 | `Frames_DecodeOpenRejectsMalformed` |  | ✅ |
| 81 | `Frames_ListRoundTrip` |  | ✅ |
| 82 | `Frames_OpenRoundTrip` |  | ✅ |
| 83 | `Scope_EmptyRejectsEverything` |  | ✅ |
| 84 | `Scope_RejectsOutOfScopePaths` | (relative: "../outside.txt") | ✅ |
| 85 | `Scope_RejectsOutOfScopePaths` | (relative: "..\\..\\Windows\\file") | ✅ |
| 86 | `Scope_RejectsOutOfScopePaths` | (relative: "") | ✅ |
| 87 | `Scope_RejectsOutOfScopePaths` | (relative: "/etc/passwd") | ✅ |
| 88 | `Scope_RejectsOutOfScopePaths` | (relative: "\\\\?\\C:\\secret") | ✅ |
| 89 | `Scope_RejectsOutOfScopePaths` | (relative: "\\\\server\\share\\file") | ✅ |
| 90 | `Scope_RejectsOutOfScopePaths` | (relative: "C:\\Windows\\System32\\config\\SAM") | ✅ |
| 91 | `Scope_RejectsOutOfScopePaths` | (relative: "file.txt:stream") | ✅ |
| 92 | `Scope_RejectsOutOfScopePaths` | (relative: "sub\\..\\..\\escape") | ✅ |
| 93 | `Scope_RejectsSiblingWithSharedPrefix` |  | ✅ |
| 94 | `Scope_ResolvesRelativePathWithinRoot` |  | ✅ |
| 95 | `Scope_RootItselfIsResolvable` |  | ✅ |
| 96 | `Scope_ToRelativeRoundTrips` |  | ✅ |

#### `KeyStoreTests`（`KeyStoreTests.cs`，6 个）

| # | 用例 | 结果 |
|---:|---|---|
| 97 | `Cross_Process_Decryption_Works` | ✅ |
| 98 | `FileSystemAcl_Locks_Down_Keystore_Directory` | ✅ |
| 99 | `FileSystemAcl_Locks_Down_Keystore_File` | ✅ |
| 100 | `LoadOrCreate_Creates_File_With_Dpapi_LocalMachine_Scope` | ✅ |
| 101 | `LoadOrCreate_Generates_Stable_Keypair_Across_Calls` | ✅ |
| 102 | `Regenerate_Produces_New_Keypair` | ✅ |

#### `MediaChannelTests`（`MediaChannelTests.cs`，17 个）

| # | 用例 | 结果 |
|---:|---|---|
| 103 | `MediaFlowPayload_RejectsShortPayload` | ✅ |
| 104 | `MediaFlowPayload_RoundTrips` | ✅ |
| 105 | `Queue_Clear_EmptiesQueue` | ✅ |
| 106 | `Queue_DropOldest_DiscardsOldestFrame` | ✅ |
| 107 | `Queue_PreferInput_DropsOldestMouseMove_NotNewFrame` | ✅ |
| 108 | `Queue_PreferInput_WhenNoMouseMove_DropsNewFrameAndKeepsKeys` | ✅ |
| 109 | `Queue_TryDequeueOnEmpty_ReturnsFalse` | ✅ |
| 110 | `Queue_WithinCapacity_NeverDrops` | ✅ |
| 111 | `Router_Disconnect_IsReflectedInStatus` | ✅ |
| 112 | `Router_ForwardsDesktopConfig_FromAgentToClient` | ✅ |
| 113 | `Router_ForwardsDesktopVideo_FromAgentToClient` | ✅ |
| 114 | `Router_ForwardsInput_FromClientToAgent` | ✅ |
| 115 | `Router_IgnoresWrongDirectionFrames` | ✅ |
| 116 | `Router_LaterClient_ReplacesEarlier` | ✅ |
| 117 | `Router_SendsFlowFeedback_ToAgent` | ✅ |
| 118 | `Router_Status_ReflectsCounters` | ✅ |
| 119 | `Router_WithoutClient_DropsVideoInsteadOfBlocking` | ✅ |

#### `PairingStoreTests`（`PairingStoreTests.cs`，5 个）

| # | 用例 | 结果 |
|---:|---|---|
| 120 | `Add_Then_List_Returns_Pairing` | ✅ |
| 121 | `Add_Twice_Same_Pub_Replaces_Label` | ✅ |
| 122 | `IsPaired_Returns_True_For_Added_Pub` | ✅ |
| 123 | `Persistence_Across_New_Store_Instance` | ✅ |
| 124 | `Remove_Deletes_Pairing` | ✅ |

#### `PipeServerTests`（`PipeServerTests.cs`，24 个）

| # | 用例 | 结果 |
|---:|---|---|
| 125 | `DirectDial_Failure_Detail_Is_Propagated` | ✅ |
| 126 | `DirectDial_Forwards_Peer_Host_And_Port_To_Core` | ✅ |
| 127 | `DirectDial_Without_PeerPub_Is_InvalidParams` | ✅ |
| 128 | `FileDownload_SurfacesFailureAsStructuredResult` | ✅ |
| 129 | `FileList_ReturnsEntries` | ✅ |
| 130 | `FileProgress_把在途传输的字节数如实报回去` | ✅ |
| 131 | `FileProgress_无在途传输时返回空列表而不是null` | ✅ |
| 132 | `FileScope_ReturnsAuthorisedRoots` | ✅ |
| 133 | `FileUpload_MissingParams_ReturnsInvalidParams` | ✅ |
| 134 | `FileUpload_ReturnsTransferResult` | ✅ |
| 135 | `GetConfig_带出文件授权目录与显示器索引` | ✅ |
| 136 | `GetDeviceInfo_Roundtrip` | ✅ |
| 137 | `GetStatus_Roundtrip` | ✅ |
| 138 | `Invalid_Json_Returns_InvalidParams` | ✅ |
| 139 | `Oversized_Frame_Disconnects` | ✅ |
| 140 | `Ping_Roundtrip` | ✅ |
| 141 | `SetConfig_把文件授权目录与显示器索引透传给Core` | ✅ |
| 142 | `SetConfig_落盘成功时如实回报persisted为真` | ✅ |
| 143 | `SetConfig_落盘失败必须回报false_不能让客户端以为以后记住了` | ✅ |
| 144 | `SetConfig_ok表达的是执行成功_不是值有没有变` | ✅ |
| 145 | `SignChallenge_Base64_Roundtrip` | ✅ |
| 146 | `StartAgent_Forward_Params_To_Core` | ✅ |
| 147 | `StopAgent_Forwards` | ✅ |
| 148 | `Unknown_Method_Returns_MethodNotFound` | ✅ |

#### `RelayPinStoreTests`（`RelayPinStoreTests.cs`，8 个）

| # | 用例 | 结果 |
|---:|---|---|
| 149 | `Clear_RemovesPin` | ✅ |
| 150 | `ConcurrentRecordFirstUse_DoesNotCrash` | ✅ |
| 151 | `Get_UnknownHost_ReturnsNull` | ✅ |
| 152 | `List_ReturnsAllEntries` | ✅ |
| 153 | `RecordFirstUse_DifferentPin_ReturnsDifferent` | ✅ |
| 154 | `RecordFirstUse_PerHost_Isolated` | ✅ |
| 155 | `RecordFirstUseThenGet_Succeeds` | ✅ |
| 156 | `Reload_PersistsAcrossInstances` | ✅ |

#### `RelaySessionE2ETests`（`RelaySessionE2ETests.cs`，3 个）

| # | 用例 | 结果 |
|---:|---|---|
| 157 | `ParseDeviceId_AcceptsHexAndBase64_RejectsGarbage` | ✅ |
| 158 | `TwoInstances_EstablishE2E_AndCompleteControlRoundTrip` | ✅ |
| 159 | `TwoInstances_FileTransfer_OverEncryptedSession` | ✅ |

#### `RelayTransportSelectionTests`（`RelayTransportSelectionTests.cs`，6 个）

| # | 用例 | 结果 |
|---:|---|---|
| 160 | `Https_Scheme_PrefersQuicAndFallsBackToTcpTls` | ✅ |
| 161 | `Null_Relay_Throws` | ✅ |
| 162 | `Quic_Scheme_WithoutQuic_ThrowsInsteadOfSilentFallback` | ✅ |
| 163 | `Quic_Scheme_WithQuicAvailable_SelectsQuic` | ✅ |
| 164 | `Tls_Scheme_AlwaysSelectsTcpTls` | ✅ |
| 165 | `Unknown_Scheme_Throws` | ✅ |

#### `RelayTrustPolicyTests`（`RelayPinStoreTests.cs`，5 个）

| # | 用例 | 结果 |
|---:|---|---|
| 166 | `AcceptAny_AlwaysTrue` | ✅ |
| 167 | `DifferentCert_Rejected` | ✅ |
| 168 | `FirstConnect_PinAndAccept` | ✅ |
| 169 | `NoCertificate_Rejected` | ✅ |
| 170 | `SameCert_SecondConnect_Accepts` | ✅ |

#### `ServiceConfigPersistenceTests`（`ServiceConfigPersistenceTests.cs`，20 个）

| # | 用例 | 参数 | 结果 |
|---:|---|---|---|
| 171 | `不带参数时_自动读出dataDir里的持久化配置` |  | ✅ |
| 172 | `负数显示器索引被夹回0` |  | ✅ |
| 173 | `空白路径被丢弃_重复路径去重` |  | ✅ |
| 174 | `空目录里没有serviceJson_安静按没有默认值处理` |  | ✅ |
| 175 | `落盘位置是dataDir下的serviceJson` |  | ✅ |
| 176 | `落盘用snake_case字段名` |  | ✅ |
| 177 | `命令行给了fileScope_覆盖落盘值` |  | ✅ |
| 178 | `命令行给了monitor_覆盖落盘值` |  | ✅ |
| 179 | `目录不存在时写入也能成功` |  | ✅ |
| 180 | `文件不存在返回null而不是抛异常` |  | ✅ |
| 181 | `文件损坏返回null而不是抛异常` |  | ✅ |
| 182 | `写入后能原样读回` |  | ✅ |
| 183 | `monitor出现在帮助文本里` |  | ✅ |
| 184 | `monitor非法取值被拒绝且给出退出码2` | (bad: "-1") | ✅ |
| 185 | `monitor非法取值被拒绝且给出退出码2` | (bad: "1.5") | ✅ |
| 186 | `monitor非法取值被拒绝且给出退出码2` | (bad: "abc") | ✅ |
| 187 | `monitor接受非零索引` |  | ✅ |
| 188 | `monitor默认0必须盖掉落盘的非零值_否则界面复位不回去` |  | ✅ |
| 189 | `monitor默认是0即主显示器` |  | ✅ |
| 190 | `monitor缺值被拒绝` |  | ✅ |

#### `ServiceCoreDirectDialTests`（`ServiceCoreDirectDialTests.cs`，16 个）

| # | 用例 | 结果 |
|---:|---|---|
| 191 | `DialDirectAsync_Rejects_Malformed_PeerPub` | ✅ |
| 192 | `DialDirectAsync_Unpaired_Peer_Is_Rejected_Locally` | ✅ |
| 193 | `DialDirectAsync_Without_Dialer_Fails_Explicitly` | ✅ |
| 194 | `GetConfig_ActiveRelayUrl_Matches_Configured_When_Unchanged` | ✅ |
| 195 | `GetConfig_Reports_ActiveRelayUrl_Separately_From_Configured` | ✅ |
| 196 | `GetStatus_DirectActiveSessions_Counts_Outbound_Sessions` | ✅ |
| 197 | `SetConfig_存了相同的值也是成功` | ✅ |
| 198 | `SetConfig_非法monitorIndex不能留下半套配置` | ✅ |
| 199 | `SetConfig_非法relayUrl同样不能留下半套配置` | ✅ |
| 200 | `SetConfig_正常落盘时persisted为真且无多余提示` | ✅ |
| 201 | `SetConfig_值变了要如实回报changed` | ✅ |
| 202 | `SetConfig_值没变但落盘失败_仍然必须给出提示` | ✅ |
| 203 | `SetConfig_Changing_DirectPort_Is_Immediate_No_Restart` | ✅ |
| 204 | `SetConfig_Changing_RelayUrl_Reports_RequiresRestart` | ✅ |
| 205 | `SetConfig_Invalid_RelayUrl_Throws` | ✅ |
| 206 | `SetConfig_Same_RelayUrl_Is_Not_A_Change` | ✅ |

#### `SessionPumpFramingTests`（`SessionPumpFramingTests.cs`，3 个）

| # | 用例 | 结果 |
|---:|---|---|
| 207 | `FragmentAcrossReads_IsReassembled` | ✅ |
| 208 | `IllegalOuterLenZero_AbortsSession` | ✅ |
| 209 | `OversizedOuterLen_AbortsSession` | ✅ |

### `Client.Tests`（141 个）

#### `DirectEndpointTests`（`DirectEndpointTests.cs`，20 个）

| # | 用例 | 参数 | 结果 |
|---:|---|---|---|
| 1 | `Invalid_Inputs_Are_Rejected` | (input: "   ", why: "空白") | ✅ |
| 2 | `Invalid_Inputs_Are_Rejected` | (input: ":47200", why: "主机为空") | ✅ |
| 3 | `Invalid_Inputs_Are_Rejected` | (input: "", why: "空") | ✅ |
| 4 | `Invalid_Inputs_Are_Rejected` | (input: "[::1:47200", why: "IPv6 缺右括号") | ✅ |
| 5 | `Invalid_Inputs_Are_Rejected` | (input: "[::1]47200", why: "IPv6 缺冒号") | ✅ |
| 6 | `Invalid_Inputs_Are_Rejected` | (input: "192.168.1.20:", why: "端口为空") | ✅ |
| 7 | `Invalid_Inputs_Are_Rejected` | (input: "192.168.1.20:0", why: "端口 0") | ✅ |
| 8 | `Invalid_Inputs_Are_Rejected` | (input: "192.168.1.20:65536", why: "端口越界") | ✅ |
| 9 | `Invalid_Inputs_Are_Rejected` | (input: "192.168.1.20:abc", why: "端口非数字") | ✅ |
| 10 | `Invalid_Inputs_Are_Rejected` | (input: "192.168.1.20", why: "缺端口") | ✅ |
| 11 | `Parse_Throws_With_Reason` |  | ✅ |
| 12 | `ToString_RoundTrips_And_Brackets_Ipv6` |  | ✅ |
| 13 | `Valid_Inputs_Parse` | (input: " 192.168.1.20:47200 ", expectedHost: "192.168.1.20", expectedPort: 47200) | ✅ |
| 14 | `Valid_Inputs_Parse` | (input: "[::1]:47200", expectedHost: "::1", expectedPort: 47200) | ✅ |
| 15 | `Valid_Inputs_Parse` | (input: "[fe80::1]:80", expectedHost: "fe80::1", expectedPort: 80) | ✅ |
| 16 | `Valid_Inputs_Parse` | (input: "10.0.0.1:1", expectedHost: "10.0.0.1", expectedPort: 1) | ✅ |
| 17 | `Valid_Inputs_Parse` | (input: "10.0.0.1:65535", expectedHost: "10.0.0.1", expectedPort: 65535) | ✅ |
| 18 | `Valid_Inputs_Parse` | (input: "192.168.1.20:47200", expectedHost: "192.168.1.20", expectedPort: 47200) | ✅ |
| 19 | `Valid_Inputs_Parse` | (input: "localhost:47200", expectedHost: "localhost", expectedPort: 47200) | ✅ |
| 20 | `Valid_Inputs_Parse` | (input: "relay.example.com:443", expectedHost: "relay.example.com", expectedPort: 443) | ✅ |

#### `FileProgressTests`（`FileProgressTests.cs`，13 个）

| # | 用例 | 结果 |
|---:|---|---|
| 21 | `传输成功_即使中途没轮询到进度也报已完成字节` | ✅ |
| 22 | `传输结束_不会留下还在跑的孤儿轮询` | ✅ |
| 23 | `传输完成后_ProgressText显示真实字节而不是百分比` | ✅ |
| 24 | `多个匹配项_取百分比最大的那条` | ✅ |
| 25 | `进度轮询在传输到达终态之后才返回_不能把完成状态又改成未知` | ✅ |
| 26 | `路径分隔符不同_也该匹配上同一次传输` | ✅ |
| 27 | `轮询抛异常_不让传输失败只把进度标成未知` | ✅ |
| 28 | `上传中_进度取自Service回报的真实分块字节` | ✅ |
| 29 | `同名不同方向_不会把上传的进度安到下载头上` | ✅ |
| 30 | `下载中_方向按receiving匹配而不是sending` | ✅ |
| 31 | `ProgressText_拿不到进度时不出现任何百分比` | ✅ |
| 32 | `Service报不出来进度_转不确定态而不是假装0` | ✅ |
| 33 | `Service给出的百分比超出0到100_被夹住` | ✅ |

#### `LanDialWiringTests`（`UiHonestyTests.cs`，6 个）

| # | 用例 | 结果 |
|---:|---|---|
| 34 | `地址格式非法时给出明确提示而不是静默返回` | ✅ |
| 35 | `控制端未放行入站端口时仍能拨号` | ✅ |
| 36 | `ConnectLan_DialFailure_Does_Not_Navigate_To_Remote` | ✅ |
| 37 | `ConnectLan_Dials_The_Peer_And_Passes_Endpoint` | ✅ |
| 38 | `ConnectLan_Invalid_Endpoint_Dials_Nothing` | ✅ |
| 39 | `ConnectLan_When_Direct_Not_Opened_Still_Dials_And_Surfaces_Failure` | ✅ |

#### `MediaFramingTests`（`MediaFramingTests.cs`，7 个）

| # | 用例 | 结果 |
|---:|---|---|
| 40 | `Encode_Rejects_Payload_Over_Wire_Limit` | ✅ |
| 41 | `Encode_Writes_BigEndian_Length_Then_FrameType` | ✅ |
| 42 | `Reader_Handles_Multiple_Frames_In_One_Append` | ✅ |
| 43 | `Reader_Keeps_Remainder_After_Partial_Frame_Then_Completes` | ✅ |
| 44 | `Reader_Reassembles_Fragmented_Arrival` | ✅ |
| 45 | `Reader_Throws_On_Illegal_Length_Prefix` | ✅ |
| 46 | `TryReadHeader_Rejects_Zero_And_OutOfRange_Length` | ✅ |

#### `MediaPayloadCodecTests`（`MediaPayloadCodecTests.cs`，17 个）

| # | 用例 | 结果 |
|---:|---|---|
| 47 | `DesktopConfigPayload_RoundTrips` | ✅ |
| 48 | `DesktopVideoChunk_Decode_Rejects_Short_Payload` | ✅ |
| 49 | `DesktopVideoChunk_RoundTrips_Flags_And_Data` | ✅ |
| 50 | `InputKey_RoundTrips_Scancode_And_Extended_Flag` | ✅ |
| 51 | `InputMouseButton_RoundTrips` | ✅ |
| 52 | `InputMouseMove_RoundTrips_And_Clamps_To_Permille` | ✅ |
| 53 | `InputWheel_Clamps_To_Int16` | ✅ |
| 54 | `MediaChannelFraming_Uses_FrameType_From_Shared_Constants` | ✅ |
| 55 | `MouseMoveCoalescer_Does_Not_Exceed_Rate` | ✅ |
| 56 | `MouseMoveCoalescer_Flush_Returns_False_When_Nothing_Pending` | ✅ |
| 57 | `MouseMoveCoalescer_Throttles_Burst_To_One_Send` | ✅ |
| 58 | `NullFrameSource_Produces_Frames_That_Change_Over_Time` | ✅ |
| 59 | `Nv12ToBgra_Maps_Limited_Range_Endpoints` | ✅ |
| 60 | `Nv12ToBgra_Neutral_Chroma_Produces_Grey` | ✅ |
| 61 | `SessionStatsPayload_RoundTrips_Including_Negative_Rtt` | ✅ |
| 62 | `VideoFragmentAssembler_Concatenates_Until_LastFragment` | ✅ |
| 63 | `VideoFragmentAssembler_Drops_Residue_When_Sequence_Changes` | ✅ |

#### `PeerFingerprintStoreTests`（`PeerFingerprintStoreTests.cs`，9 个）

| # | 用例 | 结果 |
|---:|---|---|
| 64 | `After_Confirm_Same_Key_Is_AutoApproved` | ✅ |
| 65 | `Atomic_Write_Leaves_No_Tmp_File_Behind` | ✅ |
| 66 | `Changed_PublicKey_Requires_ReConfirmation` | ✅ |
| 67 | `Clear_Resets_All_Trust` | ✅ |
| 68 | `ComputeFingerprint_Is_Deterministic_And_Grouped` | ✅ |
| 69 | `Confirm_Persists_Across_Store_Instances` | ✅ |
| 70 | `Corrupt_File_Fails_Safe_To_Empty_Store` | ✅ |
| 71 | `First_Use_Requires_Confirmation` | ✅ |
| 72 | `NeedsConfirmation_Treats_Empty_Key_As_Unconfirmed` | ✅ |

#### `PipeRpcClientTests`（`PipeRpcClientTests.cs`，11 个）

| # | 用例 | 结果 |
|---:|---|---|
| 73 | `CallAsync_RoundTrips_Against_Real_PipeServer` | ✅ |
| 74 | `CallAsync_Throws_Structured_PipeRpcException_On_Error_Response` | ✅ |
| 75 | `CallRawAsync_Correlates_Request_And_Response_Id` | ✅ |
| 76 | `CallRawAsync_Serializes_Concurrent_Calls_Over_One_Connection` | ✅ |
| 77 | `CallRawAsync_Surfaces_Error_As_Structured_Error_Not_Throw` | ✅ |
| 78 | `ConnectAsync_When_Server_Not_Running_Throws_PipeUnavailable` | ✅ |
| 79 | `EncodeFrame_MultiByte_Length_Is_BigEndian` | ✅ |
| 80 | `EncodeFrame_Rejects_Body_Over_MaxFrameBytes` | ✅ |
| 81 | `EncodeFrame_Writes_BigEndian_Length_Header` | ✅ |
| 82 | `ReadFrameAsync_Rejects_Oversized_Declared_Length` | ✅ |
| 83 | `ReadFrameAsync_Rejects_Zero_Length` | ✅ |

#### `SessionExistenceGateTests`（`UiHonestyTests.cs`，10 个）

| # | 用例 | 参数 | 结果 |
|---:|---|---|---|
| 84 | `WaitForRemoteSession_Direct_Fails_Without_Active_Session` |  | ✅ |
| 85 | `WaitForRemoteSession_Direct_Succeeds_With_Active_Session` |  | ✅ |
| 86 | `WaitForRemoteSession_NonEstablished_States_All_Rejected` | (state: "closed") | ✅ |
| 87 | `WaitForRemoteSession_NonEstablished_States_All_Rejected` | (state: "error:transport") | ✅ |
| 88 | `WaitForRemoteSession_NonEstablished_States_All_Rejected` | (state: "handshaking") | ✅ |
| 89 | `WaitForRemoteSession_NonEstablished_States_All_Rejected` | (state: "sigma") | ✅ |
| 90 | `WaitForRemoteSession_Polls_Until_Session_Appears` |  | ✅ |
| 91 | `WaitForRemoteSession_Relay_Fails_While_Offline` |  | ✅ |
| 92 | `WaitForRemoteSession_Relay_Succeeds_Only_When_E2E_Established` |  | ✅ |
| 93 | `WaitForRemoteSession_Service_Unavailable_Fails_Fast` |  | ✅ |

#### `SettingsRestartHintTests`（`UiHonestyTests.cs`，7 个）

| # | 用例 | 结果 |
|---:|---|---|
| 94 | `LoadAsync_Highlights_Active_Url_Differing_From_Configured` | ✅ |
| 95 | `LoadAsync_No_Pending_Change_When_Urls_Match` | ✅ |
| 96 | `SaveAsync_落盘失败且需要重启_两条提示都要在` | ✅ |
| 97 | `SaveAsync_落盘失败时绝不能说已保存` | ✅ |
| 98 | `SaveAsync_值没变也算成功_不能被当成失败` | ✅ |
| 99 | `SaveAsync_When_Restart_Required_Says_So_Explicitly` | ✅ |
| 100 | `SaveAsync_Without_Restart_Keeps_Plain_Message` | ✅ |

#### `ViewModelTests`（`ViewModelTests.cs`，33 个）

| # | 用例 | 参数 | 结果 |
|---:|---|---|---|
| 101 | `Devices_Approves_Relay_After_Confirmation_And_Then_AutoApproves` |  | ✅ |
| 102 | `Devices_Connect_Without_Selection_Returns_NoDeviceSelected` |  | ✅ |
| 103 | `Devices_Lan_Mode_Does_Not_Require_Fingerprint` |  | ✅ |
| 104 | `Devices_Lan_Mode_Rejects_Invalid_Endpoint` |  | ✅ |
| 105 | `Devices_Refresh_Reports_Service_Unavailable` |  | ✅ |
| 106 | `Devices_Refuses_Relay_Connect_Until_Fingerprint_Confirmed` |  | ✅ |
| 107 | `Devices_Unpair_Forwards_Key_And_Removes_Row` |  | ✅ |
| 108 | `Files_ConflictPolicy_Maps_To_Wire_Values` |  | ✅ |
| 109 | `Files_Empty_Scope_Is_Reported_As_All_Rejected` |  | ✅ |
| 110 | `Files_Failed_Transfer_Marks_Item_Failed` |  | ✅ |
| 111 | `Files_Passes_Selected_ConflictPolicy_To_Download` |  | ✅ |
| 112 | `Files_Passes_Selected_ConflictPolicy_To_Upload` |  | ✅ |
| 113 | `Files_Pause_Resume_Toggles_State` |  | ✅ |
| 114 | `Main_StatusBar_Shows_Controlled_State_And_Stats` |  | ✅ |
| 115 | `Remote_Error_Then_Retry` |  | ✅ |
| 116 | `Remote_StateMachine_Full_Happy_Path_With_Recovery` |  | ✅ |
| 117 | `Remote_StateMachine_Rejects_Illegal_Transition` |  | ✅ |
| 118 | `Remote_Stats_And_Codec_Backend_Update` |  | ✅ |
| 119 | `Remote_Waiting_For_Local_Login_Only_From_Connecting` |  | ✅ |
| 120 | `Settings_Direct_Port_Range_Validated` |  | ✅ |
| 121 | `Settings_Refuses_To_Save_Invalid_Relay_Url` |  | ✅ |
| 122 | `Settings_Relay_Certificate_Requires_First_Connect_Confirmation` |  | ✅ |
| 123 | `Settings_RelayUrl_Property_Exposes_Validation` |  | ✅ |
| 124 | `Settings_Saves_Valid_Relay_Url_And_Port` |  | ✅ |
| 125 | `Settings_Validates_Relay_Url` | (url: "   ", expected: False) | ✅ |
| 126 | `Settings_Validates_Relay_Url` | (url: "", expected: False) | ✅ |
| 127 | `Settings_Validates_Relay_Url` | (url: "ftp://x", expected: False) | ✅ |
| 128 | `Settings_Validates_Relay_Url` | (url: "https://", expected: False) | ✅ |
| 129 | `Settings_Validates_Relay_Url` | (url: "https://relay.example.com", expected: True) | ✅ |
| 130 | `Settings_Validates_Relay_Url` | (url: "not a url", expected: False) | ✅ |
| 131 | `Settings_Validates_Relay_Url` | (url: "quic://1.2.3.4:443", expected: True) | ✅ |
| 132 | `Settings_Validates_Relay_Url` | (url: "relay.example.com", expected: False) | ✅ |
| 133 | `Settings_Validates_Relay_Url` | (url: "tls://host.local:8443", expected: True) | ✅ |

#### `WpfSmokeTests`（`WpfSmokeTests.cs`，8 个）

| # | 用例 | 结果 |
|---:|---|---|
| 134 | `App_Type_Is_A_Real_Wpf_Application` | ✅ |
| 135 | `Client_Defaults_To_Pipe_Named_Default_Not_The_DataDir_Instance` | ✅ |
| 136 | `ClientOptions_Parses_Pipe_Overrides` | ✅ |
| 137 | `MainWindow_Default_Constructor_Renders` | ✅ |
| 138 | `MainWindow_Real_Settings_Tab_Renders_With_Its_Own_DataContext` | ✅ |
| 139 | `MainWindow_Renders_Without_Live_Service` | ✅ |
| 140 | `SettingsView_Binds_ActiveRelayUrl_Without_Crash` | ✅ |
| 141 | `Xaml_Bindings_Target_Properties_That_Can_Accept_TwoWay` | ✅ |

### `DirectHandshake.Tests`（41 个）

#### `DirectDialerTests`（`DirectDialerTests.cs`，15 个）

| # | 用例 | 参数 | 结果 |
|---:|---|---|---|
| 1 | `CloseAllSessions_Terminates_Outbound_Session` |  | ✅ |
| 2 | `CloseSessionsFor_Revokes_Only_Targeted_Peer` |  | ✅ |
| 3 | `DialAsync_Blank_Host_Is_Rejected_Without_Network` | (host: "   ", port: 47200) | ✅ |
| 4 | `DialAsync_Blank_Host_Is_Rejected_Without_Network` | (host: "", port: 47200) | ✅ |
| 5 | `DialAsync_Establishes_Real_Session_And_Server_Sees_Peer` |  | ✅ |
| 6 | `DialAsync_Nothing_Listening_Fails_Without_Hanging` |  | ✅ |
| 7 | `DialAsync_Out_Of_Range_Port_Is_Rejected` | (port: -1) | ✅ |
| 8 | `DialAsync_Out_Of_Range_Port_Is_Rejected` | (port: 0) | ✅ |
| 9 | `DialAsync_Out_Of_Range_Port_Is_Rejected` | (port: 100000) | ✅ |
| 10 | `DialAsync_Out_Of_Range_Port_Is_Rejected` | (port: 65536) | ✅ |
| 11 | `DialAsync_Populates_Outbound_Session_Table` |  | ✅ |
| 12 | `DialAsync_Unpaired_Peer_Fails_Fast_With_Clear_Reason` |  | ✅ |
| 13 | `DialAsync_Wrong_Length_DeviceId_Is_Rejected` |  | ✅ |
| 14 | `OnSessionEstablished_Fires_Before_Pump_Starts` |  | ✅ |
| 15 | `Second_Dial_Supersedes_Previous_Outbound_Session` |  | ✅ |

#### `DirectHandshakeTests`（`DirectHandshakeTests.cs`，8 个）

| # | 用例 | 结果 |
|---:|---|---|
| 16 | `DuplicateConnect_SupersedesPreviousSession` | ✅ |
| 17 | `OnSessionEstablished_InvokedBeforePumpStarts` | ✅ |
| 18 | `PairedDevices_HandshakeSucceeds` | ✅ |
| 19 | `PairedHandshake_FrameRoundTrip` | ✅ |
| 20 | `Revocation_ClosesDirectSessionImmediately` | ✅ |
| 21 | `Revocation_CloseSessionsFor_KicksActiveSession` | ✅ |
| 22 | `RoleAssignment_IsComplementary_RegardlessOfIdOrder` | ✅ |
| 23 | `UnpairedDevice_HandshakeRejected` | ✅ |

#### `DirectTransportTests`（`DirectTransportTests.cs`，2 个）

| # | 用例 | 结果 |
|---:|---|---|
| 24 | `Tcp_DeviceIdExchange_Succeeds` | ✅ |
| 25 | `Tcp_Unpaired_DeviceIdRejected` | ✅ |

#### `FirewallPolicyTests`（`FirewallPolicyTests.cs`，16 个）

| # | 用例 | 参数 | 结果 |
|---:|---|---|---|
| 26 | `AddFailure_DoesNotRecordEnabled` |  | ✅ |
| 27 | `AddRule_Idempotent` |  | ✅ |
| 28 | `ApplyPortChange_NotElevated_ReturnsFalseAndKeepsOldPort` |  | ✅ |
| 29 | `ApplyPortChange_WhenNotEnabled_OnlyUpdatesRecordedPort` |  | ✅ |
| 30 | `BuildAddRuleArgs_HasExpectedShape` |  | ✅ |
| 31 | `NotElevated_DoesNotTouchFirewallNorRegistry` |  | ✅ |
| 32 | `ParseLocalPort_HandlesLocalisedOutput` | (stdout: "规则名称:  X\r\n本地端口:                            47201"···, expected: 47201) | ✅ |
| 33 | `ParseLocalPort_HandlesLocalisedOutput` | (stdout: "LocalPort:  47200,47201\r\n", expected: 47200) | ✅ |
| 34 | `ParseLocalPort_HandlesLocalisedOutput` | (stdout: "Rule Name: X\r\n", expected: null) | ✅ |
| 35 | `ParseLocalPort_HandlesLocalisedOutput` | (stdout: "Rule Name: X\r\nLocalPort:                        "···, expected: 47200) | ✅ |
| 36 | `PortChange_ReconciledRules` |  | ✅ |
| 37 | `RemoveRule_NotExists_IsIdempotentSuccess` |  | ✅ |
| 38 | `Repair_ReappliesRulesWhenEnabled` |  | ✅ |
| 39 | `Repair_SkipsWhenDisabled` |  | ✅ |
| 40 | `SetEnabled_AddsBothTcpAndUdp` |  | ✅ |
| 41 | `SetEnabled_Disable_RemovesBothRules` |  | ✅ |

### `Agent.Tests`（137 个）

#### `AccessLostRecoveryTests`（`AccessLostRecoveryTests.cs`，6 个）

| # | 用例 | 结果 |
|---:|---|---|
| 1 | `Dispose_IsIdempotent` | ✅ |
| 2 | `EnsurePipeline_WhenFactoryFails_ReturnsFalseWithError` | ✅ |
| 3 | `EnsurePipeline_WhenPipelineAlreadyExists_IsIdempotent` | ✅ |
| 4 | `RebuildAndEnsure_AfterDispose_ThrowObjectDisposed` | ✅ |
| 5 | `TryRebuild_WhenFactoryFails_ReportsRetryStateAndKeepsCurrentNull` | ✅ |
| 6 | `TryRebuild_WhenFactorySucceeds_DisposesOldPipelineAndNotifiesRecovered` | ✅ |

#### `AdaptiveBitrateTests`（`AdaptiveBitrateTests.cs`，20 个）

| # | 用例 | 结果 |
|---:|---|---|
| 7 | `Current_AlwaysMatchesLevel` | ✅ |
| 8 | `DegradeThenRecover_IsSymmetric` | ✅ |
| 9 | `EncodeOverBudget_IsTreatedAsPressure` | ✅ |
| 10 | `ForLevel_ClampsOutOfRangeLevels` | ✅ |
| 11 | `ForLevel_LevelZeroEqualsConfiguredMaximums` | ✅ |
| 12 | `ForLevel_RespectsConfiguredFloors` | ✅ |
| 13 | `InputPending_IsTreatedAsPressure` | ✅ |
| 14 | `Ladder_DropsResolutionOnlyAfterBitrateAndFps` | ✅ |
| 15 | `Ladder_IsMonotoneNonIncreasing_OnAllThreeAxes` | ✅ |
| 16 | `Ladder_LevelZeroIsFullQuality` | ✅ |
| 17 | `NegativeElapsed_IsTreatedAsZero` | ✅ |
| 18 | `NeutralSamples_DoNotResetGoodStreak` | ✅ |
| 19 | `NeutralSamples_DoNotTriggerDegrade` | ✅ |
| 20 | `NewController_StartsAtFullQuality` | ✅ |
| 21 | `Pressure_DegradesAtMostToMaxLevel` | ✅ |
| 22 | `Pressure_DegradesOneStepPerCooldown` | ✅ |
| 23 | `Pressure_DoesNotDegradeBeforeCooldownElapsed` | ✅ |
| 24 | `Recovery_GoodStreakIsResetByPressure` | ✅ |
| 25 | `Recovery_RequiresConsecutiveGoodSamples` | ✅ |
| 26 | `Reset_ReturnsToFullQuality` | ✅ |

#### `AgentRuntimeTests`（`AgentRuntimeTests.cs`，15 个）

| # | 用例 | 参数 | 结果 |
|---:|---|---|---|
| 27 | `Cli_RejectsOutOfRangeFps` | (fps: "0") | ✅ |
| 28 | `Cli_RejectsOutOfRangeFps` | (fps: "500") | ✅ |
| 29 | `Cli_RejectsTooLowBitrate` |  | ✅ |
| 30 | `Cli_Resident_Defaults` |  | ✅ |
| 31 | `Cli_Run_And_MediaPipe_Parse` |  | ✅ |
| 32 | `Cli_Serve_IsAliasForRun` |  | ✅ |
| 33 | `Resident_AppliesFlowFeedback_ByDegradingQuality` |  | ✅ |
| 34 | `Resident_EmitsConfigAndDecodableAccessUnits` |  | ✅ |
| 35 | `Resident_EmitsSessionStats` |  | ✅ |
| 36 | `Resident_HandlesInputEvents_WithoutInjectingWhenNoInject` |  | ✅ |
| 37 | `Resident_IgnoresUnknownInboundFrames` |  | ✅ |
| 38 | `Resident_InputDropFeedback_DegradesQuality` |  | ✅ |
| 39 | `Resident_Reconnects_WhenPipeAppearsLate` |  | ✅ |
| 40 | `Resident_Rotation90_ReportsSwappedDimensions` |  | ✅ |
| 41 | `Resident_StopsCleanly_OnCancellation` |  | ✅ |

#### `DuplicationCaptureTests`（`DuplicationCaptureTests.cs`，3 个）

| # | 用例 | 结果 |
|---:|---|---|
| 42 | `Capture_DisposeIsIdempotent` | ✅ |
| 43 | `Capture_InitializesAndAcquiresRealFrame` | ✅ |
| 44 | `ListMonitors_ReturnsAtLeastOneMonitorWithSaneGeometry` | ✅ |

#### `FrameConverterTests`（`FrameConverterTests.cs`，18 个）

| # | 用例 | 参数 | 结果 |
|---:|---|---|---|
| 45 | `BgraToNv12_Black2x2_IsLimitedRangeBlack` |  | ✅ |
| 46 | `BgraToNv12_ChromaIsAveragedOver2x2Block` |  | ✅ |
| 47 | `BgraToNv12_GreyRamp_LumaMatchesAndChromaIsNeutral` | (v: 0, expectedY: 16) | ✅ |
| 48 | `BgraToNv12_GreyRamp_LumaMatchesAndChromaIsNeutral` | (v: 102, expectedY: 104) | ✅ |
| 49 | `BgraToNv12_GreyRamp_LumaMatchesAndChromaIsNeutral` | (v: 153, expectedY: 147) | ✅ |
| 50 | `BgraToNv12_GreyRamp_LumaMatchesAndChromaIsNeutral` | (v: 204, expectedY: 191) | ✅ |
| 51 | `BgraToNv12_GreyRamp_LumaMatchesAndChromaIsNeutral` | (v: 255, expectedY: 235) | ✅ |
| 52 | `BgraToNv12_GreyRamp_LumaMatchesAndChromaIsNeutral` | (v: 51, expectedY: 60) | ✅ |
| 53 | `BgraToNv12_Primaries_MatchExpectedNv12` | (r: 0, g: 0, b: 255, expectedY: 32, expectedCb: 240, expectedCr: 118) | ✅ |
| 54 | `BgraToNv12_Primaries_MatchExpectedNv12` | (r: 0, g: 255, b: 0, expectedY: 173, expectedCb: 42, expectedCr: 26) | ✅ |
| 55 | `BgraToNv12_Primaries_MatchExpectedNv12` | (r: 255, g: 0, b: 0, expectedY: 63, expectedCb: 102, expectedCr: 240) | ✅ |
| 56 | `BgraToNv12_RejectsOddOrZeroDimensions` | (width: 0, height: 4) | ✅ |
| 57 | `BgraToNv12_RejectsOddOrZeroDimensions` | (width: 3, height: 4) | ✅ |
| 58 | `BgraToNv12_RejectsOddOrZeroDimensions` | (width: 4, height: 3) | ✅ |
| 59 | `BgraToNv12_RespectsRowStride` |  | ✅ |
| 60 | `BgraToNv12_White2x2_IsLimitedRangeWhite` |  | ✅ |
| 61 | `Nv12ToBgra_RoundTripsGreyExactly` |  | ✅ |
| 62 | `Nv12ToBgra_RoundTripsPrimariesWithinTolerance` |  | ✅ |

#### `FrameRotationTests`（`FrameRotationTests.cs`，26 个）

| # | 用例 | 参数 | 结果 |
|---:|---|---|---|
| 63 | `IsValidAngle_OnlyAcceptsRightAngles` | (degrees: -90, expected: False) | ✅ |
| 64 | `IsValidAngle_OnlyAcceptsRightAngles` | (degrees: 0, expected: True) | ✅ |
| 65 | `IsValidAngle_OnlyAcceptsRightAngles` | (degrees: 180, expected: True) | ✅ |
| 66 | `IsValidAngle_OnlyAcceptsRightAngles` | (degrees: 270, expected: True) | ✅ |
| 67 | `IsValidAngle_OnlyAcceptsRightAngles` | (degrees: 360, expected: False) | ✅ |
| 68 | `IsValidAngle_OnlyAcceptsRightAngles` | (degrees: 45, expected: False) | ✅ |
| 69 | `IsValidAngle_OnlyAcceptsRightAngles` | (degrees: 90, expected: True) | ✅ |
| 70 | `ListMonitors_ReportsValidRotation` |  | ✅ |
| 71 | `Rotate_RejectsInvalidAngle` |  | ✅ |
| 72 | `Rotate_RespectsSourceStridePadding` |  | ✅ |
| 73 | `Rotate_ZeroDegrees_ReturnsSameInstance` |  | ✅ |
| 74 | `Rotate180_MapsPixelsCorrectly` |  | ✅ |
| 75 | `Rotate270_Clockwise_MapsPixelsCorrectly` |  | ✅ |
| 76 | `Rotate90_Clockwise_MapsPixelsCorrectly` |  | ✅ |
| 77 | `Rotate90_FourTimes_ReturnsToOriginal` |  | ✅ |
| 78 | `Rotate90_Then270_ReturnsToOriginal` |  | ✅ |
| 79 | `RotatedSize_SwapsOnlyWhenNeeded` | (w: 3, h: 2, deg: 0, ew: 3, eh: 2) | ✅ |
| 80 | `RotatedSize_SwapsOnlyWhenNeeded` | (w: 3, h: 2, deg: 180, ew: 3, eh: 2) | ✅ |
| 81 | `RotatedSize_SwapsOnlyWhenNeeded` | (w: 3, h: 2, deg: 270, ew: 2, eh: 3) | ✅ |
| 82 | `RotatedSize_SwapsOnlyWhenNeeded` | (w: 3, h: 2, deg: 90, ew: 2, eh: 3) | ✅ |
| 83 | `RotateToNv12_ProducesSwappedDimensionsAndCorrectBufferSize` |  | ✅ |
| 84 | `RotateToNv12_ZeroDegrees_KeepsDimensions` |  | ✅ |
| 85 | `SwapsDimensions_OnlyFor90And270` | (degrees: 0, expected: False) | ✅ |
| 86 | `SwapsDimensions_OnlyFor90And270` | (degrees: 180, expected: False) | ✅ |
| 87 | `SwapsDimensions_OnlyFor90And270` | (degrees: 270, expected: True) | ✅ |
| 88 | `SwapsDimensions_OnlyFor90And270` | (degrees: 90, expected: True) | ✅ |

#### `InputInjectorTests`（`InputInjectorTests.cs`，3 个）

| # | 用例 | 结果 |
|---:|---|---|
| 89 | `InjectMode_IsReported` | ✅ |
| 90 | `NoInject_AllOperationsReportSuccess_AndPerformNoInjection` | ✅ |
| 91 | `NoInject_MoveMouseWithInvalidDesktopSize_StillFails` | ✅ |

#### `LoopbackRoundTripTests`（`LoopbackRoundTripTests.cs`，4 个）

| # | 用例 | 结果 |
|---:|---|---|
| 92 | `EncodeDecodeRoundTrip_PixelsMatchWithinTolerance` | ✅ |
| 93 | `Encoder_FlushReturnsBufferedAccessUnits` | ✅ |
| 94 | `Encoder_ReportsBackendAndFallsBackToSoftware` | ✅ |
| 95 | `FirstAccessUnit_ContainsSpsPpsAndIdr` | ✅ |

#### `PipeChannelTests`（`PipeChannelTests.cs`，8 个）

| # | 用例 | 结果 |
|---:|---|---|
| 96 | `CallAsync_ErrorResponse_IsSurfacedNotThrown` | ✅ |
| 97 | `CallAsync_PingRoundTrip_AgainstNamedPipeServer` | ✅ |
| 98 | `EncodeFrame_IsBigEndian` | ✅ |
| 99 | `EncodeFrame_RejectsBodyOverMaxFrame` | ✅ |
| 100 | `GetDeviceInfoAsync_DeserializesSnakeCaseFields` | ✅ |
| 101 | `ReadFrame_RejectsOversizedLength` | ✅ |
| 102 | `ReadFrame_ReturnsNull_WhenPeerClosedWithoutWriting` | ✅ |
| 103 | `WriteThenReadFrame_RoundTrips` | ✅ |

#### `ProgramOptionsTests`（`ProgramOptionsTests.cs`，16 个）

| # | 用例 | 参数 | 结果 |
|---:|---|---|---|
| 104 | `InjectionGuard_AllowsInjectWhenNoInjectAlsoGiven` |  | ✅ |
| 105 | `InjectionGuard_AllowsPlainNoInjectAndPlainStatusReporting` |  | ✅ |
| 106 | `InjectionGuard_RejectsInjectWithoutNoInject` |  | ✅ |
| 107 | `Parse_Defaults_AreStable` |  | ✅ |
| 108 | `Parse_HelpAndListMonitors_AreRecognised` |  | ✅ |
| 109 | `Parse_InvalidArguments_ThrowArgumentException` | (commandLine: "--bogus") | ✅ |
| 110 | `Parse_InvalidArguments_ThrowArgumentException` | (commandLine: "--monitor -1") | ✅ |
| 111 | `Parse_InvalidArguments_ThrowArgumentException` | (commandLine: "--pattern-size -4x10") | ✅ |
| 112 | `Parse_InvalidArguments_ThrowArgumentException` | (commandLine: "--pattern-size 0x0") | ✅ |
| 113 | `Parse_InvalidArguments_ThrowArgumentException` | (commandLine: "--pattern-size 640") | ✅ |
| 114 | `Parse_InvalidArguments_ThrowArgumentException` | (commandLine: "--pipe") | ✅ |
| 115 | `Parse_NoHardwareEncoder_DisablesPreferHardware` |  | ✅ |
| 116 | `Parse_PatternSize_SupportsBothSeparatorCases` | (commandLine: "--pattern-size 640x480", width: 640, height: 480) | ✅ |
| 117 | `Parse_PatternSize_SupportsBothSeparatorCases` | (commandLine: "--pattern-size=1920X1080", width: 1920, height: 1080) | ✅ |
| 118 | `Parse_PipeName_SupportsBothForms` | (first: "--pipe", second: "custom.pipe", expected: "custom.pipe") | ✅ |
| 119 | `Parse_PipeName_SupportsBothForms` | (first: "--pipe=custom.pipe", second: null, expected: "custom.pipe") | ✅ |

#### `QualityIntegrationTests`（`QualityIntegrationTests.cs`，15 个）

| # | 用例 | 参数 | 结果 |
|---:|---|---|---|
| 120 | `Cli_Rotation_DefaultsToZero` |  | ✅ |
| 121 | `Cli_Rotation_ParsesValidAngles` | (degrees: 0) | ✅ |
| 122 | `Cli_Rotation_ParsesValidAngles` | (degrees: 180) | ✅ |
| 123 | `Cli_Rotation_ParsesValidAngles` | (degrees: 270) | ✅ |
| 124 | `Cli_Rotation_ParsesValidAngles` | (degrees: 90) | ✅ |
| 125 | `Cli_Rotation_RejectsInvalidAngles` | (value: "-90") | ✅ |
| 126 | `Cli_Rotation_RejectsInvalidAngles` | (value: "360") | ✅ |
| 127 | `Cli_Rotation_RejectsInvalidAngles` | (value: "45") | ✅ |
| 128 | `DegradedResolution_IsAcceptedByEncoder` |  | ✅ |
| 129 | `DegradeLadder_ProducesMonotoneEncoderSettings` |  | ✅ |
| 130 | `Loopback_OddDimensionsAfterRotation_IsSkippedWithReason` |  | ✅ |
| 131 | `Loopback_RejectsInvalidRotation_WithoutTouchingCodec` |  | ✅ |
| 132 | `Loopback_WithRotation180_KeepsDimensions` |  | ✅ |
| 133 | `Loopback_WithRotation270_SwapsDimensions` |  | ✅ |
| 134 | `Loopback_WithRotation90_SwapsDimensionsAndKeepsPixelsClose` |  | ✅ |

#### `SessionWatcherTests`（`SessionWatcherTests.cs`，3 个）

| # | 用例 | 结果 |
|---:|---|---|
| 135 | `CanCaptureDesktop_IsConsistentWithState` | ✅ |
| 136 | `GetState_ReturnsSaneSnapshot_AndNeverThrows` | ✅ |
| 137 | `NoConsoleSessionSentinel_MatchesWts` | ✅ |

### `Panel.Tests`（129 个）

#### `MainViewModelTests`（`MainViewModelTests.cs`，59 个）

| # | 用例 | 结果 |
|---:|---|---|
| 1 | `保存设置写回全部字段` | ✅ |
| 2 | `被控端配对失败时也不能只剩一句绿色已就绪` | ✅ |
| 3 | `被控端一键准备_共享目录的默认值必须留在最终提示里` | ✅ |
| 4 | `初始化读不到公钥时引导语不许盖掉错误` | ✅ |
| 5 | `初始化后能读到本机公钥与地址` | ✅ |
| 6 | `初始化时服务没跑起来就不能说已就绪` | ✅ |
| 7 | `初始化时服务确实在跑才可以说已在运行` | ✅ |
| 8 | `打开控制界面成功时提示已启动` | ✅ |
| 9 | `打开控制界面找不到时给错误提示` | ✅ |
| 10 | `防火墙操作失败时报错并带输出` | ✅ |
| 11 | `防火墙完全未放行时文案是未放行` | ✅ |
| 12 | `防火墙状态文案区分只开了一条规则` | ✅ |
| 13 | `非法端口回退到默认47200` | ✅ |
| 14 | `非零显示器索引的说明要跟索引对上` | ✅ |
| 15 | `服务意外退出时给出警告并停止显示运行中` | ✅ |
| 16 | `服务在监听但端口未放行时不把直连显示成已关闭` | ✅ |
| 17 | `负的显示器索引当场夹回0_输入框和说明不许自相矛盾` | ✅ |
| 18 | `公钥解析失败时报错而不是留下空串` | ✅ |
| 19 | `公钥为空时拒绝配对` | ✅ |
| 20 | `共享目录以分号分隔并去空白` | ✅ |
| 21 | `忙碌期间命令不可执行` | ✅ |
| 22 | `没填公钥和填错了要给不同的提示` | ✅ |
| 23 | `跑完一键准备也不能碰到真实的APPDATA配置` | ✅ |
| 24 | `配对成功后刷新状态以更新已配对数` | ✅ |
| 25 | `配对成功时不该再挂一条还没配对的提示` | ✅ |
| 26 | `配对成功时提示并且提醒对方也要配` | ✅ |
| 27 | `配对失败时显示退出码与错误首行` | ✅ |
| 28 | `启动服务后状态变为运行中并刷新状态` | ✅ |
| 29 | `切到主控端选项卡会同步角色_并在下一次保存时落盘` | ✅ |
| 30 | `切换防火墙后必须刷新状态_否则这个开关永远关不掉` | ✅ |
| 31 | `切换后刷新状态失败_不能盖掉切换自己的结果` | ✅ |
| 32 | `切选项卡只是换视图_不会顺手改直连与代理开关` | ✅ |
| 33 | `日志超过500行时截断防止内存无界增长` | ✅ |
| 34 | `停止服务后状态变为未运行` | ✅ |
| 35 | `未提权时放行防火墙会请求提权而不是直接失败` | ✅ |
| 36 | `选项卡索引与角色一一对应_主控端在左` | ✅ |
| 37 | `一键准备被控端会开直连与注入代理并起服务` | ✅ |
| 38 | `一键准备被控端会顺手配对主控端` | ✅ |
| 39 | `一键准备被控端会填默认共享目录` | ✅ |
| 40 | `一键准备被控端会自动切到被控端选项卡` | ✅ |
| 41 | `一键准备被控端没填公钥时明说还没配对` | ✅ |
| 42 | `一键准备被控端在提权重启前就配好对` | ✅ |
| 43 | `一键准备被控端在未提权时只提权不起服务` | ✅ |
| 44 | `一键准备控制端会关掉直连与注入代理` | ✅ |
| 45 | `一键准备控制端没公钥时提示要填但仍起服务` | ✅ |
| 46 | `一键准备控制端时已有公钥会顺手配对` | ✅ |
| 47 | `一键准备主控端会自动切到主控端选项卡` | ✅ |
| 48 | `已放行时再点一次是关闭` | ✅ |
| 49 | `已提权时直接执行放行` | ✅ |
| 50 | `用户自己填了共享目录就不再替他决定` | ✅ |
| 51 | `越界的选项卡索引会被夹回有效范围_不会把面板搞成空白` | ✅ |
| 52 | `载入设置到界面时同步角色与各开关` | ✅ |
| 53 | `载入设置时选项卡会落到上次用的那一页` | ✅ |
| 54 | `找不到Service时给出错误横幅而不是抛异常` | ✅ |
| 55 | `中继状态文案可读` | ✅ |
| 56 | `主控端配对失败时不能只剩一句绿色已就绪` | ✅ |
| 57 | `状态查不到时显示服务未运行` | ✅ |
| 58 | `printConfig非零退出码时错误信息含退出码` | ✅ |
| 59 | `Quic文案跟着真实状态走` | ✅ |

#### `PanelSettingsTests`（`PanelSettingsTests.cs`，16 个）

| # | 用例 | 结果 |
|---:|---|---|
| 60 | `保存后可原样读回` | ✅ |
| 61 | `被控端角色默认开直连与注入代理` | ✅ |
| 62 | `管道名与Service的命名规则一致` | ✅ |
| 63 | `控制端角色默认关直连与注入代理` | ✅ |
| 64 | `末段为盘符根时退化为default` | ✅ |
| 65 | `末尾带斜杠不影响实例名` | ✅ |
| 66 | `默认实例名为default` | ✅ |
| 67 | `切角色不动显示器索引` | ✅ |
| 68 | `实例名取路径末段并小写` | ✅ |
| 69 | `文件不存在时返回默认值而不是抛异常` | ✅ |
| 70 | `文件损坏时返回默认值而不是抛异常` | ✅ |
| 71 | `显示器索引默认是0即主显示器` | ✅ |
| 72 | `显示器索引能落盘并读回` | ✅ |
| 73 | `重复应用同一角色报告无改动` | ✅ |
| 74 | `Clone带得走显示器索引` | ✅ |
| 75 | `Clone是深拷贝` | ✅ |

#### `PanelWpfSmokeTests`（`PanelWpfSmokeTests.cs`，18 个）

| # | 用例 | 结果 |
|---:|---|---|
| 76 | `被控端也有对方的公钥输入框和配对按钮` | ✅ |
| 77 | `被控端页带着要发给主控端的公钥与地址` | ✅ |
| 78 | `本机服务与高级设置放在选项卡之外_两个角色共用` | ✅ |
| 79 | `点选卡会把角色同步给ViewModel` | ✅ |
| 80 | `角色称呼统一用主控端_不混用控制端` | ✅ |
| 81 | `控制端专属功能都在主控端选项卡下` | ✅ |
| 82 | `两个角色选项卡都存在且主控端在左` | ✅ |
| 83 | `两页的对方公钥输入框绑的是同一个字段` | ✅ |
| 84 | `提权提示在两处说法一致` | ✅ |
| 85 | `主窗口逐个选项卡渲染_无绑定错误` | ✅ |
| 86 | `OnClosing_必须先问再拆_不能让用户点否之后变僵尸` | ✅ |
| 87 | `Quic那行绑定状态而不是写死可用` | ✅ |
| 88 | `ViewModel_命令异常必须有人接_不能变成没人观察的Task异常` | ✅ |
| 89 | `Xaml_两个一键准备按钮必须绑Command_不能绑Click` | ✅ |
| 90 | `Xaml_没有把字面量和Binding混写在同一个Text属性里` | ✅ |
| 91 | `Xaml_每个绑定的目标属性都必须真实存在` | ✅ |
| 92 | `Xaml_默认TwoWay的绑定目标必须能接受写回` | ✅ |
| 93 | `Xaml_选项卡是双向绑定_点了会同步回角色` | ✅ |

#### `PipeRpcTests`（`PipeRpcTests.cs`，5 个）

| # | 用例 | 结果 |
|---:|---|---|
| 94 | `服务返回协议级错误时TryGetStatus返回null` | ✅ |
| 95 | `管道名为空时抛ArgumentException而不是静默返回` | ✅ |
| 96 | `请求帧以大端u32长度开头且内容是合法PipeRequest` | ✅ |
| 97 | `TryGetStatus_管道不存在时返回null而不是抛异常` | ✅ |
| 98 | `TryGetStatus_在服务返回状态时解出DTO` | ✅ |

#### `ServiceCliTests`（`ServiceCliTests.cs`，21 个）

| # | 用例 | 参数 | 结果 |
|---:|---|---|---|
| 99 | `FirewallSetArgs_端口与开关正确` | (port: 47200, enable: False, expected: "off") | ✅ |
| 100 | `FirewallSetArgs_端口与开关正确` | (port: 47200, enable: True, expected: "on") | ✅ |
| 101 | `FirewallSetArgs_端口与开关正确` | (port: 47500, enable: True, expected: "on") | ✅ |
| 102 | `FirewallStatusArgs_带端口且不写注册表` |  | ✅ |
| 103 | `PairArgs_公钥原样透传` |  | ✅ |
| 104 | `PrintConfigArgs_带dataDir且不误开console` |  | ✅ |
| 105 | `ResolveServiceExe_同目录下的exe可被发现` |  | ✅ |
| 106 | `ResolveServiceExe_优先用设置里指定的路径` |  | ✅ |
| 107 | `ResolveServiceExe_找不到时返回null而不是抛异常` |  | ✅ |
| 108 | `RunArgs_被控端带enableDirect与injectAgent` |  | ✅ |
| 109 | `RunArgs_端口紧跟在directPort之后` |  | ✅ |
| 110 | `RunArgs_多屏设置不被fileScope挤掉` |  | ✅ |
| 111 | `RunArgs_非零索引成对出现` |  | ✅ |
| 112 | `RunArgs_负数索引不产出monitorFlag` |  | ✅ |
| 113 | `RunArgs_空白fileScopeRoots被跳过` |  | ✅ |
| 114 | `RunArgs_空relayUrl不产出relayUrlFlag` |  | ✅ |
| 115 | `RunArgs_控制端不带enableDirect与injectAgent` |  | ✅ |
| 116 | `RunArgs_每个fileScopeRoots各出一个flag` |  | ✅ |
| 117 | `RunArgs_始终带console与dataDir` |  | ✅ |
| 118 | `RunArgs_索引为0也照样产出monitorFlag` |  | ✅ |
| 119 | `RunArgs_有relayUrl时成对出现` |  | ✅ |

#### `ServiceOutputParserTests`（`ServiceOutputParserTests.cs`，10 个）

| # | 用例 | 结果 |
|---:|---|---|
| 120 | `短公钥取全部做缩写` | ✅ |
| 121 | `防火墙JSON前后夹日志行仍能解析` | ✅ |
| 122 | `非JSON输入返回null而不是当成已放行` | ✅ |
| 123 | `解析防火墙状态JSON` | ✅ |
| 124 | `解析printConfig输出` | ✅ |
| 125 | `空输出解析为无效身份而不是抛异常` | ✅ |
| 126 | `缺公钥字段时判定为无效` | ✅ |
| 127 | `缺TCP规则时FullyOpen为假` | ✅ |
| 128 | `长公钥缩写取前8位` | ✅ |
| 129 | `KeyStore行用冒号分隔也能解析` | ✅ |

## 本轮复审新增的用例

> 相对 `f51747e` 新增 **33** 个测试方法（从 `git diff` 自动提取，不是手写清单）。
> 它们对应的缺陷、修法与「为什么原来测不出来」见 [KnownIssues.md](./KnownIssues.md) 1.9 节。

| 新增用例 | 锁住的是什么 |
|---|---|
| `被控端配对失败时也不能只剩一句绿色已就绪` | B2 另一半：被控端路径同样必须说出配对真相 |
| `被控端一键准备_共享目录的默认值必须留在最终提示里` | 共享目录默认值以前被覆盖三次，用户从来看不到 |
| `初始化读不到公钥时引导语不许盖掉错误` | 引导语不得覆盖真错误 |
| `初始化时服务没跑起来就不能说已就绪` | 实跑截图抓到的假就绪：服务"未运行"却写"被控端就绪" |
| `初始化时服务确实在跑才可以说已在运行` | 同上，正向面 |
| `非零显示器索引的说明要跟索引对上` | MonitorIndexText 与 MonitorIndex 必须一致 |
| `负的显示器索引当场夹回0_输入框和说明不许自相矛盾` | 输入框显示 -5、旁边说明写"第 1 块屏幕"，静默矛盾 |
| `进度轮询在传输到达终态之后才返回_不能把完成状态又改成未知` | 竞态：在途 RPC 把刚设的 ProgressKnown=true 又改回 false |
| `空目录里没有serviceJson_安静按没有默认值处理` | B2：不传 --data-dir 会读真实机器的 %ProgramData% |
| `没填公钥和填错了要给不同的提示` | 二态升三态：三种情况给用户的话完全不同 |
| `跑完一键准备也不能碰到真实的APPDATA配置` | I2：单测无参 SaveSettings 会写开发者真实的 panel.json |
| `配对成功时不该再挂一条还没配对的提示` | 三态的正向面：真配上了就不该再提示"还没配对" |
| `切换防火墙后必须刷新状态_否则这个开关永远关不掉` | B1：`RefreshFirewallAsync` 自带 Begin/End，切换时被内层 Begin 挡在门外 → 只放行、关不掉 |
| `切换后刷新状态失败_不能盖掉切换自己的结果` | 刷新异常不许顶掉"操作失败"的横幅 |
| `用户自己填了共享目录就不再替他决定` | 反向：不越权改写用户已经填好的值 |
| `主控端配对失败时不能只剩一句绿色已就绪` | B2：`await PairAsync()`（void 包装）丢弃配对结果，失败被两层横幅盖成绿 |
| `monitor默认0必须盖掉落盘的非零值_否则界面复位不回去` | 守住"总是传 --monitor"这一侧 |
| `OnClosing_必须先问再拆_不能让用户点否之后变僵尸` | I1：Stop/摘事件排在确认之前，点「否」后面板变僵尸 |
| `RunArgs_索引为0也照样产出monitorFlag` | I6：>0 才传导致 2 号屏改不回主显示器，落盘的 2 复活 |
| `SaveAsync_落盘失败且需要重启_两条提示都要在` | 落盘失败 + 重启提示要同时给 |
| `SaveAsync_落盘失败时绝不能说已保存` | I4：BuildSaveMessage 从不读 Persisted |
| `SaveAsync_值没变也算成功_不能被当成失败` | M3 的客户端侧 |
| `SetConfig_存了相同的值也是成功` | M3 正向面 |
| `SetConfig_非法monitorIndex不能留下半套配置` | I3：校验排在副作用之后 → 内存半配置 + 管道层吞异常 |
| `SetConfig_非法relayUrl同样不能留下半套配置` | 同上，另一条入参 |
| `SetConfig_落盘成功时如实回报persisted为真` | I2：替身把 ok 与 persisted 绑在一个开关上，测试恒过 |
| `SetConfig_落盘失败必须回报false_不能让客户端以为以后记住了` | 落盘失败的报文以前根本构造不出来 |
| `SetConfig_正常落盘时persisted为真且无多余提示` | 正向面：真的落盘了就正常 |
| `SetConfig_值变了要如实回报changed` | changed 字段的语义 |
| `SetConfig_值没变但落盘失败_仍然必须给出提示` | BuildRestartHint 见 !requiresRestart 就 return null |
| `SetConfig_ok表达的是执行成功_不是值有没有变` | M3：ok 混着"值变没变"，同值保存被当失败 |
| `ViewModel_命令异常必须有人接_不能变成没人观察的Task异常` | 改绑 Command 后异常兜底下沉到 RunCommand |
| `Xaml_两个一键准备按钮必须绑Command_不能绑Click` | I3：绑 Click 会绕开 CanExecute=!IsBusy，第二次点照样弹"已就绪" |

## 变异测试：确认新用例真能抓到回退

> 绿灯本身不能证明用例有效。本轮把每处修复**单独回退**，确认对应用例确实变红。
>
> **13/13 全部被抓。** 其中 3 条第一版没跑成并已补做，原因在最后一行——
> 这一点值得记住：**「没找到替换点」和「编译失败」都不算抓到**。

| # | 回退的修复 | 所在项目 | 变红的用例数 | 结果 |
|---:|---|---|---:|---|
| 1 | 防火墙切换后不再刷新状态 | `Panel` | 1 | ✅ 抓到 |
| 2 | 主控端丢弃 PairAsync 返回值 | `Panel` | 3 | ✅ 抓到 |
| 3 | 配对三态退化成二态 | `Panel` | 2 | ✅ 抓到 |
| 4 | monitor 索引 0 时不传 --monitor | `Panel` | 1 | ✅ 抓到 |
| 5 | 一键准备按钮改回绑 Click | `Panel` | 1 | ✅ 抓到 |
| 6 | OnClosing 把 Stop 挪回确认之前 | `Panel` | 1 | ✅ 抓到 |
| 7 | MonitorIndex 负数不再当场夹 | `Panel` | 1 | ✅ 抓到 |
| 8 | 共享目录提示丢回会被覆盖的分支 | `Panel` | 1 | ✅ 抓到 |
| 9 | 初始化横幅不再看服务是否真在跑 | `Panel` | 1 | ✅ 抓到 |
| 10 | set_config 的 Ok 又跟着 changed 走 | `Service` | 3 | ✅ 抓到 |
| 11 | 入参校验挪回副作用之后 | `Service` | 1 | ✅ 抓到 |
| 12 | 值没变且没落盘时不给任何提示 | `Service` | 1 | ✅ 抓到 |
| 13 | BuildSaveMessage 不再读 Persisted | `Client` | 2 | ✅ 抓到 |

### 三条第一版没跑成的（以及为什么不能算数）

1. **`BuildSaveMessage` 与 `OnClosing` 两条多行替换"没找到替换点"** ——
   脚本按 Windows 换行（CRLF）拼多行匹配串，而这两个源文件是 **LF-only** 行尾，
   `Contains()` 全部 false。表象像"这个 bug 测不出来"，真相是**变异根本没生效**。
   判定前必须先量目标文件的行尾符。
2. **一键准备按钮改回 `Click` 时编译失败（CS1061）** ——
   `OnPrepareControllerClick` 处理器早已删除。这只证明"改动确实生效了"，
   **不证明护栏会拦住它**。补做方式：同一个变异里把处理器一起加回来，让它能编译，再看护栏是否变红。

> 这三件事的共同点：脚本**静默跳过**一个变异时，人很容易把"没跑"记成"测不出来"，
> 然后得出一个完全错误的结论。所以变异脚本必须自带"未生效"告警。

## 怎么重新生成本文件

```powershell
# 跑一遍全量单测，然后重新生成本文件（会覆盖 testcase.md）
pwsh -NoProfile -File tests/gen-testcase.ps1

# 只改文档措辞时，复用上一次的 trx，不用重跑测试
pwsh -NoProfile -File tests/gen-testcase.ps1 -SkipRun
```

> 手写测试清单迟早会和实际跑的东西对不上，而对不上的清单比没有更糟——
> 它会让人以为某些功能"有测试覆盖"。所以本文件是生成物，不是手写物。

