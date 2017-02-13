module SocketIoSuave.EngineIo.Engine

open SocketIoSuave
open SocketIoSuave.EngineIo.Protocol
open Suave
open Suave.CORS
open Suave.Operators
open System
open System.Security.Cryptography
open Suave.Logging
open Suave.Logging.Message

type EngineIoContext = 
    {
        Transport: string
        JsonPIndex: int option
        SessionId: string option
        SupportsBinary: bool
        IsBinary: bool
    }

    with static member FromHttp (ctx: HttpContext) = {
            Transport = ctx.request.queryParam "transport" |> Choice.defaultArg "polling"
            JsonPIndex = ctx.request.queryParam "j" |> Choice.bind Choice.parseInt |> Option.ofChoice
            SessionId = ctx.request.queryParam "sid" |> Option.ofChoice
            SupportsBinary = ctx.request.queryParam "b64" |> Choice.bind Choice.parseIntAsBool |> Choice.defaultArg false
            IsBinary = defaultArg (ctx |> Headers.getFirstHeader "content-type") "" = "application/octet-stream"
        }

type SocketId = SocketId of string
with
    override x.ToString() = match x with | SocketId s -> s

type IncomingCommunication =
    | Init of Socket
    | NewMessage of PacketMessage

and OutgoingCommunication =
    | Init of Socket
    | AddMessage of PacketMessage
    | GetMessages of AsyncReplyChannel<Payload>

and Socket =
    {
        Id: SocketId
        Transport: Transport
        IncomingMessages: MailboxProcessor<IncomingCommunication>
        OutgoingMessages: MailboxProcessor<OutgoingCommunication>
    }

[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Socket =
    /// Send a packet to the client. This is a non-blocking write.
    let send msg socket = socket.OutgoingMessages.Post(OutgoingCommunication.AddMessage(msg))

    let getOutgoing timeout socket =
        // TODO: If we timeout, kill the socket
        socket.OutgoingMessages.PostAndAsyncReply ((fun c -> GetMessages(c)), timeout)

    let addIncoming message socket =
        socket.IncomingMessages.Post (IncomingCommunication.NewMessage message)

type EngineIoConfig =
    {
        Path: string
        Upgrades: string[] // "websocket"
        CORSConfig: CORSConfig
        PingTimeout: TimeSpan
        PingInterval: TimeSpan
        CookieName: string option
        CookiePath: string option
        CookieHttpOnly: bool
        RandomNumberGenerator: RandomNumberGenerator
        
        /// Packets sent along with the handshake
        InitialPackets: PacketMessage list
        onPacket: Socket -> PacketMessage -> Async<unit>
        getPacket: HttpContext -> Async<PacketMessage option>
        getPayload: HttpContext -> Async<Payload option>
    }

    with static member empty = {
            Path = "/engine.io/"
            CookieName = Some "io"
            CookiePath = Some "/"
            CookieHttpOnly = false
            Upgrades = Array.empty
            CORSConfig =
                { defaultCORSConfig with
                    allowedMethods = InclusiveOption.Some([HttpMethod.GET; HttpMethod.POST])
                    allowedUris = InclusiveOption.All
                    allowCookies = true }
            PingTimeout = TimeSpan.FromSeconds(60.)
            PingInterval = TimeSpan.FromSeconds(25.)
            RandomNumberGenerator = RandomNumberGenerator.Create()
            InitialPackets = []
            onPacket = (fun socket message -> async {
                match message with
                | Ping data -> socket |> Socket.send (Pong(data))
                | _ -> ()
                return ()
            })
            getPacket = (fun _ -> async { return None })
            getPayload = (fun _ -> async { return None })
        }

let setIoCookie config value : WebPart =
    warbler(fun _ ->
        match config.CookieName with
        | Some(cookieName) ->
            let cookie =
                { HttpCookie.empty with
                    name = cookieName
                    value = value
                    path = config.CookiePath
                    httpOnly = match config.CookiePath with | Some _ -> config.CookieHttpOnly | None -> false
                }
            Cookie.setCookie cookie
        | None -> 
            succeed
    )

type EngineIoServer =
    {
        mutable OpenSessions: Map<SocketId, Socket>
    }

// Fixed in next Suave version, https://github.com/SuaveIO/suave/pull/575
let removeBuggyCorsHeader: WebPart =
    fun ctx -> async {
        let finalHeaders =
            ctx.response.headers
            |> List.filter (fun (k,v) -> k <> "Access-Control-Allow-Credentials" || v = "True")
            |> List.map(fun (k,v) -> if k = "Access-Control-Allow-Credentials" then k,v.ToLower() else k,v)
        return Some({ ctx with response = { ctx.response with headers = finalHeaders } })
    }


// Adapted from https://github.com/socketio/socket.io/pull/1333
let disableXSSProtectionForIE: WebPart =
    warbler (fun ctx ->
        let ua = ctx |> Headers.getFirstHeader "User-Agent"
        match ua with
        | Some(ua) when ua.Contains(";MSIE") || ua.Contains("Trident/") ->
            Writers.setHeader "X-XSS-Protection" "0"
        | _ -> succeed
    )

let serveEngineIo (config: EngineIoConfig) =
    let server = { OpenSessions = Map.empty }
    let idGenerator = Base64Id.create config.RandomNumberGenerator
    let respondPayload payload engineContext: WebPart = 
        if engineContext.SupportsBinary then
            Writers.setHeader "Content-Type" "text/plain; charset=UTF-8"
                >=> Successful.OK (payload |> Payload.encodeToString)
        else
            Writers.setHeader "Content-Type" "application/octet-stream"
                >=> Successful.ok (payload |> Payload.encodeToBinary |> Segment.toArray)

    let ll = Targets.create Debug [| "Bug" |]
    let createSocket socketId handleIncomming =
        let socket = {
            Id = socketId
            Transport = Polling
            IncomingMessages = MailboxProcessor<IncomingCommunication>.Start(fun inbox ->
                let rec loop (socket: Socket) = async {
                    let! msg = inbox.Receive()
                    match msg with
                    | NewMessage msg ->
                        ll.info (eventX (sprintf "Incoming %A" msg))
                        do! handleIncomming socket msg
                    | _ -> ()
                    return! loop socket
                }

                let rec initLoop () = async {
                    let! msg = inbox.Receive()
                    match msg with
                    | IncomingCommunication.Init socket -> return! loop socket
                    | _ -> return! initLoop ()
                }

                initLoop ()
                )
            OutgoingMessages = MailboxProcessor<OutgoingCommunication>.Start(fun inbox ->
                let rec loop (socket: Socket) messages (currentReplyChan: AsyncReplyChannel<Payload> option) = async {
                    let! msg = inbox.Receive()
                    match msg with
                    | AddMessage msg ->
                        match currentReplyChan with
                        | Some(chan) ->
                            ll.info (eventX (sprintf "%A AddMessage with reply channel, sending %A" socket.Id msg))
                            chan.Reply(Payload(msg::messages))
                            return! loop socket [] None
                        | None ->
                            ll.info (eventX (sprintf "%A AddMessage storing %A" socket.Id msg))
                            return! loop socket (msg::messages) None
                    | GetMessages rep ->
                        match messages, currentReplyChan with
                        | [], None->
                            ll.info (eventX (sprintf "%A GetMessages nothing yet" socket.Id))
                            return! loop socket [] (Some(rep))
                        | messages, None ->
                            ll.info (eventX (sprintf "%A GetMessages %i available" socket.Id messages.Length))
                            rep.Reply(Payload(messages))
                            return! loop socket [] None
                        | _, Some(_) ->
                            ll.error (eventX (sprintf "%A CROSS THE BEAMS" socket.Id))                            
                            failwith "Don't cross the beams !"
                    | _ ->
                        return! loop socket messages currentReplyChan
                }

                let rec initLoop () = async {
                    let! msg = inbox.Receive()
                    match msg with
                    | OutgoingCommunication.Init socket -> return! loop socket [] None
                    | _ -> return! initLoop ()
                }

                initLoop ()
            )
        }

        socket.IncomingMessages.Post(IncomingCommunication.Init(socket))
        socket.OutgoingMessages.Post(OutgoingCommunication.Init(socket))
        socket

    let getPart: WebPart = fun ctx -> async {
        let engineContext = EngineIoContext.FromHttp ctx
        match engineContext.SessionId with
        | None ->
            let socketId = idGenerator ()
            let handshake = {
                Sid = socketId;
                Upgrades = config.Upgrades;
                PingTimeout = int config.PingTimeout.TotalMilliseconds;
                PingInterval = int config.PingInterval.TotalMilliseconds }

            let socket = createSocket (SocketId socketId) config.onPacket
            server.OpenSessions <- server.OpenSessions |> Map.add socket.Id socket
            
            ctx.runtime.logger.info (eventX (sprintf "Creating session with ID %s" socketId))

            let payload = Payload(Open(handshake) :: config.InitialPackets)

            let pipeline = setIoCookie config socketId >=> respondPayload payload engineContext
            return! pipeline ctx
        | Some(sessionId) ->
            let socketId = SocketId sessionId
            match server.OpenSessions |> Map.tryFind socketId with
            | Some(socket) ->
                let! payload = socket |> Socket.getOutgoing (30 * 1000)
                for message in payload |> Payload.getMessages do
                    ctx.runtime.logger.debug (eventX (sprintf "%s <- %A" sessionId message))
                let pipeline = setIoCookie config sessionId >=> respondPayload payload engineContext
                return! pipeline ctx
            | None ->
                return! RequestErrors.BAD_REQUEST "Unknown session" ctx
    }

    let postPart: WebPart = fun ctx -> async {
        let engineContext = EngineIoContext.FromHttp ctx
        match engineContext.SessionId with
        | None -> return! RequestErrors.BAD_REQUEST "Unknown session" ctx
        | Some(sessionId) -> 
            let socketId = SocketId sessionId
            match server.OpenSessions |> Map.tryFind socketId with
            | Some(socket) ->
                let binary = engineContext.SupportsBinary
                let payload =
                    if binary then
                        ctx.request.rawForm |> Segment.ofArray |> Payload.decodeFromBinary
                    else
                        ctx.request.rawForm |> Text.Encoding.UTF8.GetString |> Payload.decodeFromString
                for message in payload |> Payload.getMessages do
                    ctx.runtime.logger.debug (eventX (sprintf "%s -> %A" sessionId message))
                    socket |> Socket.addIncoming message
                let pipeline = setIoCookie config sessionId >=> (Successful.ok [||])
                return! pipeline ctx
            | None ->
                return! RequestErrors.BAD_REQUEST "Unknown session" ctx
    }
        
    choose [
        Filters.pathStarts config.Path
            >=>
            choose [
                Filters.GET >=> getPart
                Filters.POST >=> postPart
                RequestErrors.BAD_REQUEST "O_o"
            ]
            >=> cors config.CORSConfig
            >=> removeBuggyCorsHeader
            >=> disableXSSProtectionForIE
    ]

(*
open SocketIoSuave.EngineIo.Protocol

type SocketId = SocketId of string
type Socket = unit
type EngineIoServer =
    {
        OpenSessions: Map<SocketId, Socket>
    }

type RawQueryParams =
    {
        Transport: string option
        P: string option
        B64: string option
        Sid: string option
        ContentType: string option
    }

type QueryParams = 
    {
        Transport: Transport
        JsonPIndex: int option
        SessionId: string option
        SupportsBinary: bool
        IsBinary: bool
    }

type EngineApp<'ctx> = 
    {
        getQueryParams: 'ctx -> RawQueryParams
        getStringContent: 'ctx -> string
        getBinaryContent: 'ctx -> byte[]
    }
*)