﻿module SocketIoSuave.EngineIo.Protocol.TestVectors

open Expecto
open SocketIoSuave
open SocketIoSuave.EngineIo
open SocketIoSuave.EngineIo.Protocol

let private testPacketToBinary message expected format =
    let expected = expected |> Seq.map(byte) |> Seq.toArray
    Expect.equal (message |> PacketMessageEncoder.encodeToBinary |> Segment.toArray) expected format

let private testPacketToString message expected format =
    Expect.equal (message |> PacketMessageEncoder.encodeToString) expected format

let private testPayloadToBinary messages (expected: int seq) format =
    let expected = expected |> Seq.map(byte) |> Seq.toArray
    Expect.equal (Payload(messages) |> PayloadEncoder.encodeToBinary |> Segment.toArray) expected format

let private testPayloadToString messages expected format =
    Expect.equal (Payload(messages) |> PayloadEncoder.encodeToString) expected format

let utf8 (s: string) = System.Text.Encoding.ASCII.GetBytes(s) |> Array.map int
let appendUtf8 s (arr: int seq) = Seq.concat [arr; utf8 s |> Seq.ofArray]

[<Tests>]
let tests =
    testList "encode" [
        testList "packet to string" [
            testCase "open" <| fun _ ->
                let handshake = { Sid= "xxx";Upgrades=[|"foo";"bar"|];PingTimeout=42; PingInterval=43 }
                testPacketToString (Open(handshake)) @"0{""sid"":""xxx"",""upgrades"":[""foo"",""bar""],""pingTimeout"":42,""pingInterval"":43}" "open"

            testCase "close" <| fun _ ->
                testPacketToString (Close) "1" "close"

            testCase "ping" <| fun _ ->
                testPacketToString (Ping(TextPacket("probe"))) "2probe" "ping"

            testCase "ping (binary)" <| fun _ ->
                testPacketToString (Ping(BinaryPacket([|1uy;2uy;3uy|] |> Segment.ofArray))) "b2AQID" "ping (binary)"

            testCase "pong" <| fun _ ->
                testPacketToString (Pong(TextPacket("probe"))) "3probe" "pong"

            testCase "pong (binary)" <| fun _ ->
                testPacketToString (Pong(BinaryPacket([|1uy;2uy;3uy|] |> Segment.ofArray))) "b3AQID" "pong (binary)"

            testCase "message" <| fun _ ->
                testPacketToString (Message(TextPacket("Hello world"))) "4Hello world" "message"

            testCase "message (binary)" <| fun _ ->
                testPacketToString (Message(BinaryPacket([|1uy;2uy;3uy|] |> Segment.ofArray))) "b4AQID" "message (binary)"

            testCase "close" <| fun _ ->
                testPacketToString (Upgrade) "5" "close"

            testCase "noop" <| fun _ ->
                testPacketToString (Noop) "6" "noop"
        ]

        testList "packet to binary" [
            testCase "open" <| fun _ ->
                let handshake = { Sid= "xxx";Upgrades=[|"foo";"bar"|];PingTimeout=42; PingInterval=43 }
                let handshakeStr = @"{""sid"":""xxx"",""upgrades"":[""foo"",""bar""],""pingTimeout"":42,""pingInterval"":43}"
                testPacketToBinary (Open(handshake)) ([48] |> appendUtf8 handshakeStr) "open"

            testCase "close" <| fun _ ->
                testPacketToBinary (Close) [49] "close"

            testCase "ping" <| fun _ ->
                testPacketToBinary (Ping(TextPacket("probe"))) ([50] |> appendUtf8 "probe") "ping"

            testCase "ping (binary)" <| fun _ ->
                testPacketToBinary (Ping(BinaryPacket([|1uy;2uy;3uy|] |> Segment.ofArray))) [2;1;2;3] "ping (binary)"

            testCase "pong" <| fun _ ->
                testPacketToBinary (Pong(TextPacket("probe"))) ([51] |> appendUtf8 "probe") "ping"

            testCase "pong (binary)" <| fun _ ->
                testPacketToBinary (Pong(BinaryPacket([|1uy;2uy;3uy|] |> Segment.ofArray))) [3;1;2;3] "pong (binary)"

            testCase "message" <| fun _ ->
                testPacketToBinary (Message(TextPacket("Hello world"))) ([52] |> appendUtf8 "Hello world") "message"

            testCase "message (binary)" <| fun _ ->
                testPacketToBinary (Message(BinaryPacket([|1uy;2uy;3uy|] |> Segment.ofArray))) [4;1;2;3] "message (binary)"

            testCase "upgrade" <| fun _ ->
                testPacketToBinary (Upgrade) [53] "upgrade"

            testCase "noop" <| fun _ ->
                testPacketToBinary (Noop) [54] "noop"
        ]

        testList "payload to string" [
            testCase "empty" <| fun _ ->
                testPayloadToString [] "0:" "empty"
            testCase "single message" <| fun _ ->
                testPayloadToString [Message(TextPacket("Hello world"))] "12:4Hello world" "message"
            testCase "two messages" <| fun _ ->
                testPayloadToString [Message(TextPacket("Hello"));Message(TextPacket("world"))] "6:4Hello6:4world" "2 messages"
            testCase "single binary message" <| fun _ ->
                testPayloadToString [Message(BinaryPacket([|1uy;2uy;3uy|] |> Segment.ofArray))] "6:b4AQID" "message"
        ]

        testList "payload to binary" [
            testCase "empty" <| fun _ ->
                testPayloadToBinary [] [] "empty"
            testCase "close" <| fun _ ->
                testPayloadToBinary [Close] [0x00; 0x01; 0xFF; 0x31] "[close]"
            testCase "ping" <| fun _ ->
                testPayloadToBinary [Ping(TextPacket("abc"))] [0x00; 0x04; 0xff; 0x32; 0x61; 0x62; 0x63] "[ping text]"
            testCase "ping (binary)" <| fun _ ->
                testPayloadToBinary [Ping(BinaryPacket([|1uy;2uy;3uy|] |> Segment.ofArray))] [0x01; 0x04; 0xff; 2; 1; 2; 3] "[ping binary]"
            testCase "pong" <| fun _ ->
                testPayloadToBinary [Pong(TextPacket("abc"))] [0x00; 0x04; 0xff; 0x33; 0x61; 0x62; 0x63] "[pong text]"
            testCase "pong (binary)" <| fun _ ->
                testPayloadToBinary [Pong(BinaryPacket([|1uy;2uy;3uy|] |> Segment.ofArray))] [0x01; 0x04; 0xff; 3; 1; 2; 3] "[pong binary]"
            testCase "message" <| fun _ ->
                testPayloadToBinary [Message(TextPacket("abc"))] [0x00; 0x04; 0xff; 0x34; 0x61; 0x62; 0x63] "[message text]"
            testCase "message (binary)" <| fun _ ->
                testPayloadToBinary [Message(BinaryPacket([|1uy;2uy;3uy|] |> Segment.ofArray))] [0x01; 0x04; 0xff; 4; 1; 2; 3] "[message binary]"
            testCase "upgrade" <| fun _ ->
                testPayloadToBinary [Upgrade] [0x00; 0x01; 0xFF; 0x35] "[upgrade]"
            testCase "noop" <| fun _ ->
                testPayloadToBinary [Noop] [0x00; 0x01; 0xFF; 0x36] "[noop]"
            testCase "multiple" <| fun _ ->
                testPayloadToBinary [Noop; Message(BinaryPacket([|1uy;2uy;3uy|] |> Segment.ofArray)); Close] [0x00; 0x01; 0xFF; 0x36; 0x01; 0x04; 0xff; 4; 1; 2; 3; 0x00; 0x01; 0xFF; 0x31] "[noop;message binary;close]"
        ]
    ]