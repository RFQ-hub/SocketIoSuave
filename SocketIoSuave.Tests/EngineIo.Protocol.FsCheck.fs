﻿module SocketIoSuave.EngineIo.Protocol.FsCheck

open Expecto
open FsCheck
open SocketIoSuave
open SocketIoSuave.EngineIo.Protocol

let binaryPacketRoundtrip packet =
    let encoded = packet |> PacketMessage.encodeToBinary
    let isBinary = packet |> PacketMessage.getData |> Data.requireBinary
    encoded |> PacketMessage.decodeFromBinary isBinary

let stringPacketRoundtrip packet =
    let encoded = packet |> PacketMessage.encodeToString
    encoded |> PacketMessage.decodeFromString

let genArray = Gen.arrayOf Arb.generate<byte> |> Gen.filter (isNull >> not)
let genSegment = Gen.map Segment.ofArray genArray

let dataEqual d1 d2 =
    match d1, d2 with
    | String(s1), String(s2) -> s1 = s2
    | Empty, Empty -> true
    | String(s), Empty -> isNull s || s = "" 
    | Empty, String(s) -> isNull s || s = "" 
    | Binary(d), Empty -> d.Count = 0
    | Empty, Binary(d) -> d.Count = 0
    | Binary(d1), Binary(d2) -> Array.equalsConstantTime (d1 |> Segment.toArray) (d2 |> Segment.toArray)
    | _ -> false

let packetEqual p1 p2 =
    match p1, p2 with
    | Open(h1), Open(h2) -> h1 = h2
    | Close, Close -> true
    | Ping(d1), Ping(d2) -> dataEqual d1 d2
    | Pong(d1), Pong(d2) -> dataEqual d1 d2
    | Message(d1), Message(d2) -> dataEqual d1 d2
    | Upgrade, Upgrade -> true
    | Noop, Noop -> true
    | _ -> false

let payloadEqual p1 p2 =
    let m1 = p1 |> Payload.getMessages
    let m2 = p2 |> Payload.getMessages

    List.length m1 = List.length m2
        && Seq.zip m1 m2 |> Seq.forall (fun (m1, m2) -> packetEqual m1 m2)

type ProtocolGenerators =
    static member ByteArray() = 
        { new Arbitrary<ByteSegment>() with
            override __.Generator = genSegment }

let config = { FsCheck.Config.Default with Arbitrary = [typeof<ProtocolGenerators>]; MaxTest = 10000 }

[<Tests>]
let properties =
    testList "Protocol.FsCheck" [
        testCase "xxx" <| fun _ ->
            let x = [|0uy;1uy;255uy;49uy|] |> Segment.ofArray
            let p = Payload.decodeFromBinary x
            p |> ignore
        testPropertyWithConfig config "binary packet roundtrip" <| fun p1 ->
            let p2 = p1 |> binaryPacketRoundtrip
            packetEqual p1 p2

        testPropertyWithConfig config "string packet roundtrip" <| fun p1 ->
            let p2 = p1 |> stringPacketRoundtrip
            packetEqual p1 p2

        testPropertyWithConfig config "binary payload roundtrip" <| fun p1 ->
            let encoded = p1 |> Payload.encodeToBinary
            let p2 = encoded |> Payload.decodeFromBinary
            payloadEqual p1 p2

        testPropertyWithConfig config "string payload roundtrip" <| fun p1 ->
            let encoded = p1 |> Payload.encodeToString
            let p2 = encoded |> Payload.decodeFromString
            payloadEqual p1 p2
    ]