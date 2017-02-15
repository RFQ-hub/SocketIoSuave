module SocketIoSuave.SocketIo

open Newtonsoft.Json.Linq
open SocketIoSuave
open System.Text
open Newtonsoft.Json
open System

/// Type of a socket.io packet
type PacketType =
    /// Connection request
    /// Can either be accepted (The server send back Connect) or refused (Error)
    | Connect
    
    /// A disconnection request
    | Disconnect
    
    /// A message sent from one side to the other
    | Event
    
    /// Acknowledge a previous Event that was sent with EventId set
    | Ack
    
    /// Signal that an error hapenned
    | Error

/// A socket.io packet
type Packet =
    {
        /// Type of the packet being transmited
        Type: PacketType
        
        /// Destination namespace, default is "/"
        Namespace: string
        
        /// Id of the event, if provided an ack should be sent to respond to this packet
        EventId: int option

        /// JSon data associated with the packet, can contain simple json types or byte arrays.
        Data: JToken list
    }

/// socket.io protocol encoding and decoding
module Protocol =
    /// Version of the socket.io protocol
    let version = 4
    type PacketContent = SocketIoSuave.EngineIo.Protocol.PacketContent
    type ByteSegment = System.ArraySegment<byte>

    type RawPacketType =
        | Connect
        | Disconnect
        | Event
        | Ack
        | Error
        | BinaryEvent
        | BinaryAck

    type RawPacket =
        {
            Type: RawPacketType
            Namespace: string option
            EventId: int option
            Data: JToken list
            Attachments: int option
        }

    type PartialPacket =
        {
            Packet : RawPacket
            Attachments: ByteSegment list
        }

    [<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
    module RawPacketType =
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

        let fromPacketType = function
            | PacketType.Connect -> Connect
            | PacketType.Disconnect -> Disconnect
            | PacketType.Event -> Event
            | PacketType.Ack -> Ack
            | PacketType.Error -> Error

        let toPacketType = function
            | Connect -> PacketType.Connect
            | Disconnect -> PacketType.Disconnect
            | Event -> PacketType.Event
            | BinaryEvent -> PacketType.Event
            | Ack -> PacketType.Ack
            | BinaryAck -> PacketType.Ack
            | Error ->PacketType. Error

    let private toRaw (packet: Packet) =
        {
            Type = RawPacketType.fromPacketType packet.Type
            Namespace = Some (packet.Namespace)
            EventId = packet.EventId
            Data = packet.Data
            Attachments = None
        }

    let private fromRaw (packet: RawPacket) : Packet =
        {
            Type = RawPacketType.toPacketType packet.Type
            Namespace = match packet.Namespace with | None -> "/" | Some ns -> ns
            EventId = packet.EventId
            Data = packet.Data
        }

    [<RequireQualifiedAccess>]
    module PacketEncoder =
        open System.Diagnostics

        let inline private mkPlaceholder (num: int) : JObject =
            let placeholder = new JObject()
            placeholder.Add("_placeholder", JValue true)
            placeholder.Add("num", JValue num)
            placeholder

        let rec private tokenContainsBytes (token: JToken) : bool =
            match token.Type with
            | JTokenType.Bytes -> true
            | JTokenType.Object ->  token :?> JObject |> Seq.exists tokenContainsBytes
            | JTokenType.Property -> tokenContainsBytes ((token :?> JProperty).Value)
            | JTokenType.Array -> token :?> JArray |> Seq.exists tokenContainsBytes
            | _ -> false

        let internal tokensContainsBytes : JToken seq -> bool = Seq.exists tokenContainsBytes

        [<Sealed>]
        type private ConditionalChecker private() =
            [<Conditional("DEBUG")>]
            static member ThrowIfContainsBytes (token: JToken): unit =
                if tokenContainsBytes token then 
                    invalidArg "token" "JSON contains binary data"

            [<Conditional("DEBUG")>]
            static member ThrowIfContainsBytes (tokens: JToken list): unit =
                for token in tokens do
                    ConditionalChecker.ThrowIfContainsBytes token


        let rec private deconstructJson (token: JToken) (binaryData: ByteSegment list) : JToken * ByteSegment list =
            match token.Type with
            | JTokenType.Bytes ->
                let bytes = (token :?> JValue).Value :?> byte[] |> Segment.ofArray
                let placeholder = mkPlaceholder binaryData.Length
                placeholder :> JToken, bytes :: binaryData
            | JTokenType.Object -> 
                let obj = token :?> JObject
                let mutable currentBinaryData = binaryData
                let result = JObject()
                for pair in obj do
                    let key = pair.Key
                    let value = pair.Value
                    if key = "_placeholder" then
                        invalidArg "token" "Source JSON can't contain a '_placeholder' property"
                    let newValue, newBinaryData = deconstructJson value currentBinaryData
                    result.[key] <- newValue
                    currentBinaryData <- newBinaryData
                result :> JToken, currentBinaryData
            | JTokenType.Array ->
                let arr = token :?> JArray
                let mutable currentBinaryData = binaryData
                let result = JArray()
                for i in [0..arr.Count-1] do
                    let newValue, newBinaryData = deconstructJson (arr.[i]) currentBinaryData
                    result.Add(newValue)
                    currentBinaryData <- newBinaryData
                result :> JToken, currentBinaryData
            | _ -> token, binaryData

        let private deconstruct (packet: RawPacket) : PartialPacket =
            // Remove binary content from the JSON and place it in attachments
            let finalTokens, attachments =
                packet.Data
                |> List.fold
                    (fun (tokens, attachments) originalToken ->
                        let newToken, newAttachments = deconstructJson originalToken attachments
                        (newToken::tokens), newAttachments)
                    ([], [])

            // Deduce the 'wire' type
            let realRawType  =
                match packet.Type with
                | Ack -> BinaryAck
                | Event -> BinaryEvent
                | _ -> invalidArg "packet" (sprintf "Unexpected packet type for binary deconstruction: %A" packet.Type)

            // Build the final packet as will be sent on the wire and the attachments
            {
                Packet =
                    { packet with
                        Type = realRawType
                        Data = finalTokens |> List.rev
                        Attachments = Some attachments.Length
                    }
                Attachments = attachments |> List.rev
            }

        let private encodeToString (packet: RawPacket) : string =
            let builder = StringBuilder()
            builder.Append(RawPacketType.toTypeId packet.Type) |> ignore

            if RawPacketType.isBinary packet.Type then
                let attachments = defaultArg packet.Attachments 0
                if attachments < 0 then
                    failwith "Invalid number of attachments"
                builder.Append(attachments) |> ignore
                builder.Append('-') |> ignore
            else
                ConditionalChecker.ThrowIfContainsBytes packet.Data
                   
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
            if tokensContainsBytes packet.Data then
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
            let typeId = RawPacketType.fromTypeId rawTypeId
            let buf = StringBuilder(20)
            let attachments = 
                if RawPacketType.isBinary typeId then
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
                if i < s.Length && s.[i] = '/' then
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

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Packet =
    let encode = Protocol.PacketEncoder.encode
    let ofType t = { Type = t; Namespace = "/"; EventId = None; Data = [] }