module SocketIoSuave.SocketIo.Protocol.FsCheck

open Expecto
open FsCheck
open SocketIoSuave
open SocketIoSuave.SocketIo
open Newtonsoft.Json.Linq
open System

let genByteArray = Gen.arrayOf Arb.generate<byte>
let genSegment = Gen.map Segment.ofArray (genByteArray |> Gen.filter (isNull >> not))

let genJValue =
    Gen.oneof
        [
            Arb.generate<int> |> Gen.map JValue
            Arb.generate<UInt32> |> Gen.map JValue
            Arb.generate<bool> |> Gen.map JValue
            Arb.generate<char> |> Gen.map JValue
            Arb.generate<DateTime> |> Gen.map JValue
            Arb.generate<DateTimeOffset> |> Gen.map JValue
            Arb.generate<Decimal> |> Gen.map JValue
            Arb.generate<double> |> Gen.map JValue
            Arb.generate<Guid> |> Gen.map JValue
            Arb.generate<int64> |> Gen.map JValue
            Arb.generate<single> |> Gen.map JValue
            Arb.generate<string> |> Gen.map JValue
            Arb.generate<TimeSpan> |> Gen.map JValue
            Arb.generate<UInt64> |> Gen.map JValue
            //Arb.generate<Uri> |> Gen.map JValue
            genByteArray |> Gen.map JValue
        ]

let mapGenToToken<'t when 't :> JToken> = Gen.map (fun (x: 't) -> x:> JToken)

let rec genJToken' s =
    match s with
    | 0 -> genJValue |> mapGenToToken
    | n when n > 0 ->
        Gen.oneof
            [
                genJValue |> mapGenToToken
                genJArray' (n/2) |> mapGenToToken
                genJObject' (n/2) |> mapGenToToken
            ]
    | _ -> invalidArg "s" "Only positive arguments are allowed"

and genJArray' s =
    match s with
    | 0 -> Gen.constant (JArray())
    | n when n > 0 ->
        genJToken' n
        |> Gen.arrayOf
        |> Gen.map JArray
    | _ -> invalidArg "s" "Only positive arguments are allowed"

and genJObject' s =
    match s with
    | 0 -> Gen.constant (JObject())
    | n when n > 0 ->
        gen {
            let! key = Arb.generate<string>
            let! value = genJToken' n
            return key, value
        }
        |> Gen.arrayOf
        |> Gen.map(fun arr ->
            let result = JObject()
            for (key, value) in arr do
                result.Add(key, value)
            result)
    | _ -> invalidArg "s" "Only positive arguments are allowed"

let genJToken = Gen.sized genJToken'
let genJArray = Gen.sized genJArray'
let genJObject = Gen.sized genJObject'

let genString (charGen: Gen<char>) = Gen.map string (Gen.arrayOf charGen)
let genOrNull gen = Gen.oneof [ gen; Gen.constant null]

let genValidPacket = gen {
    let! t = Arb.generate<PacketType>
    let! ns = genOrNull (genString (Arb.generate<char> |> Gen.filter ((<>) ','))) |> Gen.map ((+) "/")
    let! evId = Arb.generate<int option> |> Gen.map (function |Some x -> Some (abs x) | None -> None)
    let! data = Arb.generate<JToken list>
    return {
        Packet.Type = t
        Namespace = ns
        EventId = evId
        Data = data
    }
}

type ProtocolGenerators =
    static member JToken() = Arb.fromGen genJToken
    static member JArray() = Arb.fromGen genJArray
    static member JObject() = Arb.fromGen genJObject
    static member Packet() = Arb.fromGen genValidPacket

let config = { FsCheck.Config.Default with Arbitrary = [typeof<ProtocolGenerators>] }

[<Tests>]
let properties =
    testList "SocketIo.Protocol.FsCheck" [
        ftestCase "data ordering in binary packet" <| fun _ ->
            let encoded = PacketEncoder.encode {
                Type = PacketType.BinaryAck
                Data = [ JValue(1); JValue(2)]
                Namespace = "/"
                EventId = None
            }
            Expect.equal encoded [PacketContent.TextPacket "60-[1,2]"] ""

        ftestCase "data ordering in text packet" <| fun _ ->
            let encoded = PacketEncoder.encode {
                Type = PacketType.Ack
                Data = [ JValue(1); JValue(2)]
                Namespace = "/"
                EventId = None
            }
            Expect.equal encoded [PacketContent.TextPacket "3[1,2]"] ""

        ftestPropertyWithConfig (1413405593,296264514) config "foo" <| fun p1 ->
            let content = PacketEncoder.encode p1
            let packets, state =
                content
                |> List.fold
                    (fun (packets, decoderState) packetContent ->
                        let newPacket, newState = PacketDecoder.step packetContent decoderState
                        match newPacket with
                        | Some packet -> packet::packets, newState
                        | None -> packets, newState
                        )
                    ([], PacketDecoder.empty)
            match packets, state with
            | [p2], { PartialPacket = None } ->
                if p1.EventId <> p2.EventId then
                    false
                else if p1.Namespace <> p2.Namespace then
                    false
                else if p1.Type <> p2.Type then
                    false
                else if p1.Data.Length <> p2.Data.Length then
                    false
                else
                    let eq = p1.Data |> Seq.zip p2.Data |> Seq.forall (fun (t1, t2) -> JToken.DeepEquals(t1, t2))
                    eq
                        
            | _ -> false
    ]