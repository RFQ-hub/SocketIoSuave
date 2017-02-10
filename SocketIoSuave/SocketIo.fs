module SocketIoSuave.SocketIo

open Newtonsoft.Json.Linq
open SocketIoSuave
open System.Text
open Newtonsoft.Json
open System

type PacketType =
    | Connect
    | Disconnect
    | Event
    | Ack
    | Error
    | BinaryEvent
    | BinaryAck

type Packet =
    {
        Type: PacketType
        Namespace: string
        EventId: int option
        Data: JToken list
    }

module Protocol =
    type PacketContent = SocketIoSuave.EngineIo.Protocol.PacketContent
    type ByteSegment = System.ArraySegment<byte>

    type RawPacket =
        {
            Type: PacketType
            Namespace: string option
            EventId: int option
            Data: JToken list
            Attachments: int option
        }

    let toRaw (packet: Packet) =
        {
            Type = packet.Type
            Namespace = Some (packet.Namespace)
            EventId = packet.EventId
            Data = packet.Data
            Attachments = None
        }

    let fromRaw (packet: RawPacket) : Packet =
        {
            Type = packet.Type
            Namespace = match packet.Namespace with | None -> "/" | Some ns -> ns
            EventId = packet.EventId
            Data = packet.Data
        }

    type PartialPacket =
        {
            Packet : RawPacket
            Attachments: ByteSegment list
        }

    [<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
    module PacketType =
        let toTypeId = function
            | Connect -> 0uy
            | Disconnect -> 1uy
            | Event -> 2uy
            | Ack -> 3uy
            | Error -> 4uy
            | BinaryEvent -> 5uy
            | BinaryAck -> 6uy

        let fromTypeId = function
            | 0uy -> Connect
            | 1uy -> Disconnect
            | 2uy ->  Event
            | 3uy -> Ack
            | 4uy -> Error
            | 5uy -> BinaryEvent
            | 6uy -> BinaryAck
            | x -> failwithf "Invalid type ID %i" x

        let isBinary = function
            | BinaryEvent -> true
            | BinaryAck -> true
            | _ -> false

    [<RequireQualifiedAccess>]
    module PacketEncoder =
        let inline private mkPlaceholder (num: int) : JObject =
            let placeholder = new JObject()
            placeholder.Add("_placeholder", JValue true)
            placeholder.Add("num", JValue num)
            placeholder

        let rec private deconstructJson (token: JToken) (binaryData: ByteSegment list) : JToken * ByteSegment list =
            match token.Type with
            | JTokenType.Bytes ->
                let bytes = (token :?> JValue).Value :?> byte[] |> Segment.ofArray
                let placeholder = mkPlaceholder binaryData.Length
                placeholder :> JToken, bytes :: binaryData
            | JTokenType.Object -> 
                let obj = token :?> JObject
                let mutable currentBinaryData = binaryData
                for pair in obj do
                    let key = pair.Key
                    let value = pair.Value
                    let newValue, newBinaryData = deconstructJson value currentBinaryData
                    obj.[key] <- newValue
                    currentBinaryData <- newBinaryData
                obj :> JToken, currentBinaryData
            | JTokenType.Array ->
                let arr = token :?> JArray
                let mutable currentBinaryData = binaryData
                for i in [0..arr.Count-1] do
                    let newValue, newBinaryData = deconstructJson (arr.[i]) currentBinaryData
                    arr.[i] <- newValue
                    currentBinaryData <- newBinaryData
                arr :> JToken, currentBinaryData
            | _ -> token, binaryData

        let private deconstruct (packet: RawPacket) : PartialPacket =
            let finalTokens, finalBinaryData =
                packet.Data
                |> List.fold
                    (fun (tokens, binaryData) originalToken ->
                        let newToken, newBinaryData = deconstructJson originalToken binaryData
                        (newToken::tokens), newBinaryData)
                    ([], [])

            {
                Packet =
                    { packet with
                        Data = finalTokens |> List.rev
                        Attachments = Some finalBinaryData.Length
                    }
                Attachments = finalBinaryData |> List.rev
            }

        let private encodeToString (packet: RawPacket) : string =
            let builder = StringBuilder()
            builder.Append(PacketType.toTypeId packet.Type) |> ignore

            if PacketType.isBinary packet.Type then
                let attachments = defaultArg packet.Attachments 0
                if attachments < 0 then
                    failwith "Invalid number of attachments"
                builder.Append(attachments) |> ignore
                builder.Append('-') |> ignore
            else
                if packet.Attachments.IsSome then
                    failwith "Non binary packet with attachments"

            match packet.Namespace with
            | Some ns when not (System.String.IsNullOrEmpty(ns)) && ns <> "/" ->
                if ns.[0] <> '/' || ns.IndexOf(',') <> -1 then
                    failwith "Invalid namespace"
                builder.Append(ns) |> ignore
                builder.Append(',') |> ignore
            | _ -> ()

            match packet.EventId with
            | Some eventId ->
                if eventId < 0 then
                    failwith "Negative ID"
                builder.Append(eventId) |> ignore
            | _ -> ()

            if packet.Data.Length <> 0 then
                let dataArray = JArray(packet.Data)
                let dataString = dataArray.ToString(Formatting.None)
                builder.Append(dataString) |> ignore

            builder.ToString()

        let private encodeToBinary (packet: RawPacket) : PacketContent list =
            let deconstructed = deconstruct packet
            let stringPart = encodeToString deconstructed.Packet
            (PacketContent.TextPacket stringPart) :: (deconstructed.Attachments |> List.map PacketContent.BinaryPacket)

        let encode (packet: Packet) : PacketContent list =
            if PacketType.isBinary packet.Type then
                encodeToBinary (toRaw packet)
            else
                let str = encodeToString (toRaw packet)
                [PacketContent.TextPacket str]

    [<RequireQualifiedAccess>]
    module PacketDecoder =
        type State =
            {
                PartialPacket: PartialPacket option
            }

        let inline private isPlaceholder (obj: JObject) : bool = not (isNull obj.["_placeholder"])
        let inline private getPlaceholderNum (obj: JObject) : int = obj.Value("num")

        let rec private reconstructJson (token: JToken) (binaryData: ByteSegment list) : JToken =
            match token.Type with
            | JTokenType.Object -> 
                let obj = token :?> JObject
                let isPlaceholder = isPlaceholder obj
                if isPlaceholder then
                    let num = getPlaceholderNum obj
                    let array = binaryData.[num] |> Segment.toArray
                    JValue array :> JToken
                else
                    for pair in obj do
                        let key = pair.Key
                        let value = pair.Value
                        obj.[key] <- reconstructJson value binaryData
                    obj :> JToken
            | JTokenType.Array ->
                let arr = token :?> JArray
                for i in [0..arr.Count-1] do
                    arr.[i] <- reconstructJson (arr.[i]) binaryData
                arr :> JToken
            | _ -> token
      
        let private reconstruct (packet: PartialPacket) : Packet =
            let finalTokens =
                packet.Packet.Data
                |> List.map(fun originalToken -> reconstructJson originalToken packet.Attachments)

            { (packet.Packet |> fromRaw) with Data = finalTokens }

        let inline private isNumber (c: char) = c >= '0' && c <= '9'

        let private decodeString (s: string) : RawPacket =
            if s.Length = 0 then
                failwith "Too small"

            let mutable i = 0
            let rawTypeId = Byte.Parse(string s.[i])
            i <- i + 1
            let typeId = PacketType.fromTypeId rawTypeId
            let buf = StringBuilder(20)
            let attachments = 
                if PacketType.isBinary typeId then
                    while i < s.Length && s.[i] <> '-'  do
                        buf.Append(s.[i]) |> ignore
                        i <- i + 1
                    if i = s.Length then
                        failwith "Illegal attachments"
                    i <- i + 1
                    let attachments = Int32.Parse(buf.ToString())
                    buf.Clear() |> ignore
                    Some attachments
                else
                    None

            let ns =
                if s.[i] = '/' then
                    while i < s.Length && s.[i] <> ','  do
                        buf.Append(s.[i]) |> ignore
                        i <- i + 1
                    if i = s.Length then
                        failwith "Illegal namespace"
                    i <- i + 1
                    let ns = buf.ToString()
                    buf.Clear() |> ignore
                    Some ns
                else
                    None

            let eventId =
                while i < s.Length && isNumber (s.[i])  do
                    buf.Append(s.[i]) |> ignore
                    i <- i + 1
                if buf.Length = 0 then
                    None
                else
                    Some (Int32.Parse(buf.ToString()))

            let data =
                if i < s.Length then
                    let jArray = JArray.Parse(s.Substring(i))
                    jArray |> Seq.toList
                else
                    []

            {
                Type = typeId
                Namespace = ns
                EventId = eventId
                Data = data
                Attachments = attachments
            }

        let empty = { PartialPacket = None; }

        let step (content: PacketContent) (state: State) : Packet option * State =
            match content with
            | PacketContent.Empty -> None, state
            | PacketContent.TextPacket s ->
                match state.PartialPacket with
                | Some _ -> failwith "boom, missing packets ???"
                | None ->
                    let raw = decodeString s
                    match raw.Attachments with
                    | Some(attachments) when attachments > 0 ->
                        let partial = { Packet = raw; Attachments = [] }
                        None, { state with PartialPacket = Some partial }
                    | _ -> Some (fromRaw raw), state
            | PacketContent.BinaryPacket b ->
                match state.PartialPacket with
                | None -> failwith "Why binary, not expected"
                | Some partial ->
                    let partial = { partial with Attachments = b :: partial.Attachments }
                    if partial.Attachments.Length = (defaultArg partial.Packet.Attachments 0) then
                        Some(reconstruct partial), { state with PartialPacket = None }
                    else
                        None, { state with PartialPacket = Some partial }
