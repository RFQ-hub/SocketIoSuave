module SocketIoSuave.SocketIo.Protocol.FsCheck

open Expecto
open FsCheck
open SocketIoSuave
open SocketIoSuave.SocketIo
open Newtonsoft.Json.Linq
open System
open System.Collections.Generic

let genByteArray = Gen.arrayOf Arb.generate<byte>
let genSegment = Gen.map Segment.ofArray (genByteArray |> Gen.filter (isNull >> not))

let genSimpleJValues =
    [
        Arb.generate<int> |> Gen.map JValue
        Arb.generate<UInt32> |> Gen.map JValue
        Arb.generate<bool> |> Gen.map JValue
        Arb.generate<char> |> Gen.map JValue
        //Arb.generate<DateTime> |> Gen.map JValue
        //Arb.generate<DateTimeOffset> |> Gen.map JValue
        Arb.generate<decimal> |> Gen.map JValue
        Arb.generate<double> |> Gen.filter (fun d -> (not (Double.IsInfinity(d))) && (not (Double.IsNaN(d)))) |> Gen.map JValue
        ///Arb.generate<Guid> |> Gen.map JValue
        Arb.generate<int64> |> Gen.map JValue
        Arb.generate<single> |> Gen.filter (fun d -> (not (Single.IsInfinity(d))) && (not (Single.IsNaN(d)))) |> Gen.map JValue
        Arb.generate<string> |> Gen.map JValue
        //Arb.generate<TimeSpan> |> Gen.map JValue
        Arb.generate<UInt64> |> Gen.map JValue
        //Arb.generate<Uri> |> Gen.map JValue
        genByteArray |> Gen.map JValue
    ]

let genJValue (valueGenerators: Gen<JValue> seq) = Gen.oneof valueGenerators

let mapGenToToken<'t when 't :> JToken> = Gen.map (fun (x: 't) -> x:> JToken)

let rec genJToken' valueGenerators s =
    match s with
    | 0 -> mapGenToToken (genJValue valueGenerators)
    | n when n > 0 ->
        Gen.oneof
            [
                mapGenToToken (genJValue valueGenerators)
                mapGenToToken (genJArray' valueGenerators (n/2))
                mapGenToToken (genJObject' valueGenerators (n/2))
            ]
    | _ -> invalidArg "s" "Only positive arguments are allowed"

and genJArray' valueGenerators s =
    match s with
    | 0 -> Gen.constant (JArray())
    | n when n > 0 ->
        genJToken' valueGenerators (n/2)
        |> Gen.arrayOf
        |> Gen.map JArray
    | _ -> invalidArg "s" "Only positive arguments are allowed"

and genJObject' valueGenerators s =
    match s with
    | 0 -> Gen.constant (JObject())
    | n when n > 0 ->
        gen {
            let! key = Arb.generate<string> |> Gen.filter (isNull >> not)
            let! value = genJToken' valueGenerators (n/2)
            return key, value
        }
        |> Gen.listOf
        |> Gen.map(fun arr ->
            let result = JObject()
            for (key, value) in List.distinctBy fst arr do
                result.Add(key, value)
            result)
    | _ -> invalidArg "s" "Only positive arguments are allowed"

let genJToken valueGenerators = Gen.sized (genJToken' valueGenerators)
let genJArray valueGenerators = Gen.sized (genJArray' valueGenerators)
let genJObject valueGenerators = Gen.sized (genJObject' valueGenerators)

let genString (charGen: Gen<char>) = Gen.arrayOf charGen |> Gen.map System.String
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

type PacketGenerator =
    static member Packet() = Arb.fromGen genValidPacket

type JsonGenerators =
    static member JToken() = Arb.fromGen (genJToken genSimpleJValues)
    static member JArray() = Arb.fromGen (genJArray genSimpleJValues)
    static member JObject() = Arb.fromGen (genJObject genSimpleJValues)

let config = { FsCheck.Config.Default with Arbitrary = [typeof<PacketGenerator>; typeof<JsonGenerators>] }

let objTuples (obj: JObject) = [ for pair in (obj :> IEnumerable<KeyValuePair<string,JToken>>) do yield pair.Key, pair.Value]

let rec expectJTokenEquals (t1: JToken) (t2: JToken) =
    match t1.Type, t2.Type with
    | JTokenType.Array, JTokenType.Array ->
        let b1 = (t1 :?> JArray)
        let b2 = (t1 :?> JArray)
        Expect.equal (b1.Count) (b2.Count) "Same length"
        for i in [0..b1.Count-1] do
            expectJTokenEquals (b1.[i]) (b2.[i])
    | JTokenType.Bytes, JTokenType.Bytes ->
        let b1 = (t1 :?> JValue).Value :?> byte[]
        let b2 = (t1 :?> JValue).Value :?> byte[]
        Expect.isTrue (Array.equalsConstantTime b1 b2) "Same binary arrays"
    | JTokenType.Object, JTokenType.Object ->
        let o1 = t1 :?> JObject
        let o2 = t2 :?> JObject
        Expect.equal (o1.Count) (o2.Count) "Same length"
        let o1 = objTuples o1 |> Seq.sortBy fst
        let o2 = objTuples o2 |> Seq.sortBy fst

        for ((k1,v1),(k2,v2)) in Seq.zip o1 o2 do
            Expect.equal k1 k2 "Same key"
            expectJTokenEquals v1 v2
    | JTokenType.Float, JTokenType.Float ->
        let f1 = t1.Value<double>()
        let f2 = t2.Value<double>()
        Expect.floatClose (Accuracy.medium) f1 f2 "Same float value"
    | x, y when x = y ->
        Expect.isTrue (JToken.DeepEquals(t1, t2)) "Basic value equals"
    | JTokenType.String, JTokenType.Null when isNull (t1.Value<string>()) -> ()
    | JTokenType.Null, JTokenType.String when isNull (t2.Value<string>()) -> ()
    | JTokenType.Bytes, JTokenType.Null when isNull (t1.Value<byte[]>()) -> ()
    | JTokenType.Null, JTokenType.Bytes when isNull (t2.Value<byte[]>()) -> ()
    | _, _ ->
        Expect.isFalse true "Not the same type"


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
            try
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
                        for (t1, t2) in Seq.zip p1.Data p2.Data do
                            expectJTokenEquals t1 t2
                        true
                        
                | _ -> false
            with 
            | :? ArgumentException as ex when ex.Message.Contains("JSON contains binary data") ->
                not (PacketType.isBinary p1.Type)
    ]