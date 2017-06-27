module SocketIoSuave.EngineIo.Engine

open System
open System.Security.Cryptography
open System.Collections.Generic
open Suave.Logging
open Suave.Logging.Message
open Suave
open Suave.CORS
open Suave.Operators
open Suave.Cookie
open Suave.Sockets
open Suave.Sockets.Control
open Suave.WebSocket
open Chessie.ErrorHandling
open System.Threading.Tasks
open SocketIoSuave
open SocketIoSuave.EngineIo
open SocketIoSuave.EngineIo.Protocol
open SocketIoSuave.SuaveHelpers

let private log = Log.create "SocketIoSuave.EngineIo"

type SocketId = SocketId of string
with
    override x.ToString() = match x with | SocketId s -> s

type EngineIoConfig =
    {
        Path: string
        Upgrades: Transport list
        PingTimeout: TimeSpan
        PingInterval: TimeSpan
        CookieName: string option
        CookiePath: string option
        CookieHttpOnly: bool
        RandomNumberGenerator: RandomNumberGenerator
        
        /// Packets sent along with the handshake
        InitialPackets: PacketMessage list
    }

    with static member empty = {
            Path = "/engine.io/"
            CookieName = Some "io"
            CookiePath = Some "/"
            CookieHttpOnly = false
            Upgrades = [Websocket]

            PingTimeout = TimeSpan.FromSeconds(60.)
            PingInterval = TimeSpan.FromSeconds(25.)
            RandomNumberGenerator = RandomNumberGenerator.Create()
            InitialPackets = []
        }
        
let private mkCookie (socketId: SocketId) config =
    match config.CookieName with
    | Some(cookieName) ->
        Some { HttpCookie.empty with name = cookieName; value = socketId.ToString(); path = config.CookiePath; httpOnly = config.CookieHttpOnly }
    | None -> None

let private mkHandshake socketId config =
    {
        Sid = socketId
        Upgrades = config.Upgrades |> List.map Transport.toString |> Array.ofList
        PingTimeout = int config.PingTimeout.TotalMilliseconds
        PingInterval = int config.PingInterval.TotalMilliseconds
    }

type Error =
    | UnknownSessionId
    | Unknown

type IEngineIoSocket =
    abstract member Id: SocketId with get
    abstract member Read: unit -> Async<PacketContent option>
    abstract member Send: PacketContent seq -> unit
    abstract member Send: PacketContent -> unit
    abstract member Broadcast: PacketContent seq -> unit
    abstract member Broadcast: PacketContent -> unit
    abstract member Close: unit -> unit

type private IncomingCommunication =
    | NewIncomming of PacketMessage
    | ReadIncomming of AsyncReplyChannel<PacketContent option>
    | CloseIncomming

type private OutgoingCommunication =
    | NewOutgoing of PacketMessage list
    | ReadOutgoing of AsyncReplyChannel<Payload>
    | Upgrading of WebSocket
    | Upgraded
    | CloseOutgoing

type private SocketEngineCommunication =
    {
        socketClosing: SocketId -> unit
        broadcast: (SocketId option) *  (PacketContent seq) -> unit
    }

type private EngineIoSocket(id: SocketId, pingTimeout: TimeSpan, comms: SocketEngineCommunication, handleSocket: IEngineIoSocket -> Async<unit>) as this =
    let logVerbose s = log.verbose (eventX (sprintf "{socketId} %s" s) >> setField "socketId" (id.ToString()))
    let logError s = log.error (eventX (sprintf "{socketId} %s" s) >> setField "socketId" (id.ToString()))
    let logDebug s = log.debug (eventX (sprintf "{socketId} %s" s) >> setField "socketId" (id.ToString()))
    let logWarn s = log.warn (eventX (sprintf "{socketId} %s" s) >> setField "socketId" (id.ToString()))
    
    let closeLock = new obj()
    let mutable closed = false
    let mutable transport = Polling
    let mutable task: Task = null

    let timeoutInMs = int pingTimeout.TotalMilliseconds
    let w = Diagnostics.Stopwatch()

    let pingTimeoutTimer =
        let timer = new System.Timers.Timer()
        timer.AutoReset <- false
        timer.Interval <- pingTimeout.TotalMilliseconds
        timer.Elapsed.Add(fun _ ->
            logDebug (sprintf "Ping timeout, closing socket. Since last reset= %O" w.Elapsed)
            this.Close())
        timer

    let setPingTimeout () =
        pingTimeoutTimer.Stop()
        w.Restart()
        pingTimeoutTimer.Start()

    let incomming = MailboxProcessor<IncomingCommunication>.Start(fun inbox ->
        let rec loop (messages: Queue<PacketContent>) (currentReplyChan: AsyncReplyChannel<PacketContent option> option) = async {
            let! msg = inbox.Receive()

            match msg with
            | NewIncomming msg ->
                // We're nice an consider any message as a ping for the purpose of connection timeouts
                setPingTimeout()
                logVerbose (sprintf "NewIncomming %A" msg)
                
                match msg with
                | Ping data ->
                    this.AddOutgoing([Pong(data)])
                    return! loop messages currentReplyChan
                | Open _ ->
                    logWarn "Open received during communication"
                    return! loop messages currentReplyChan
                | Upgrade ->
                    logWarn "Upgrade received on an already open transport"
                    return! loop messages currentReplyChan
                | Close -> this.Close()
                | Message content ->
                    match currentReplyChan with
                    | Some(chan) ->
                        chan.Reply(Some content)
                    | None ->
                        messages.Enqueue content

                    return! loop messages None
                | Pong _
                | Noop -> return! loop messages currentReplyChan
            | ReadIncomming rep ->
                match messages.Count = 0, currentReplyChan with
                | true, None-> return! loop messages (Some(rep))
                | false, None ->
                    let msg = messages.Dequeue()
                    rep.Reply (Some msg)
                    return! loop messages None
                | _, Some(_) ->
                    failwith "Don't cross the beams !"
            | CloseIncomming ->
                logVerbose "CloseIncomming"
                match currentReplyChan with
                | Some(chan) -> chan.Reply(None)
                | None -> ()
                return ()
        }

        loop (new Queue<PacketContent>()) None
        )

    let sendOnWebSocket (ws: WebSocket) message =
        let binary = PacketMessageEncoder.requireBinary message
        if binary then
            let bytes = PacketMessageEncoder.encodeToBinary message
            ws.send Binary bytes true |> Async.Ignore // TODO: Don't ignore errors
        else
            let bytes = PacketMessageEncoder.encodeToString message |> UTF8.bytes |> Segment.ofArray
            ws.send Text bytes true |> Async.Ignore // TODO: Don't ignore errors

    let outgoing = MailboxProcessor<OutgoingCommunication>.Start(fun inbox ->
        let rec loop messages (currentReplyChan: AsyncReplyChannel<Payload> option) (websocket: WebSocket option) (upgrading: bool) = async {
            let! msg = inbox.Receive()
            match msg with
            | NewOutgoing newMessages ->
                logVerbose (sprintf "NewOutgoing %A" newMessages)
                match websocket, upgrading with
                | None, _ ->
                    let allMessages = List.append newMessages messages
                    match currentReplyChan with
                    | Some(chan) ->
                        chan.Reply(Payload allMessages)
                        return! loop [] None websocket upgrading
                    | None ->
                        return! loop allMessages None websocket upgrading
                | Some _, true ->
                    let allMessages = List.append newMessages messages
                    return! loop allMessages None websocket upgrading
                | Some ws, false ->
                    for message in newMessages do
                        do! sendOnWebSocket ws message
                    return! loop [] None websocket upgrading
            | ReadOutgoing rep ->
                match websocket with
                | Some _ ->
                    rep.Reply(Payload([Noop]))
                    return! loop messages currentReplyChan websocket upgrading
                | None ->
                    match currentReplyChan with
                    | Some otherRep ->
                        // Two requests crossed, if the client behave normally the first one had a network error and
                        // timeouted, but just in case we still answer something
                        logWarn "[ReadOutgoing] Requests crossed, previous will get an empty payload"
                        otherRep.Reply(Payload([]))
                    | None -> ()

                    match messages with
                    | []-> return! loop [] (Some(rep)) None false
                    | messages ->
                        rep.Reply(Payload(messages))
                        return! loop [] None  None false
            | Upgrading ws ->
                match currentReplyChan with
                | Some otherRep ->
                    otherRep.Reply(Payload([Noop]))
                | None -> ()
                return! loop messages None (Some ws) true
            | Upgraded ->
                match websocket, upgrading with
                | Some ws, true ->
                    for message in messages do
                        do! sendOnWebSocket ws message
                    return! loop [] None websocket false
                | _ -> () // Logic error ?
            | CloseOutgoing ->
                logVerbose "CloseOutgoing"
                match currentReplyChan with
                | Some(chan) -> chan.Reply(Payload([Close]))
                | None -> ()
                match websocket with
                | Some(websocket) ->
                    do! sendOnWebSocket websocket Close
                    // TODO: Really close the websocket
                | None -> ()
                return ()
        }

        loop [] None None false
    )

    let onMailBoxError (x: Exception) =
        logError (sprintf "Exception, will close: %O" (x.ToString()))
        this.Close()

    do
        incomming.DefaultTimeout <- timeoutInMs * 2
        incomming.Error.Add(onMailBoxError)
        outgoing.DefaultTimeout <- timeoutInMs * 2
        outgoing.Error.Add(onMailBoxError)

    member val Id = id
    member __.Transport with get() = transport
    member __.Start() =
        setPingTimeout ()
        
        // Start the async handler for this socket on the threadpool
        task <- handleSocket (this) |> Async.StartAsTask
        
        // When the handler finishes, close the socket
        task.ContinueWith(fun _ ->
            logVerbose "Handler finished, will close"
            this.Close()) |> ignore

    member __.ReadIncomming() =
        lock closeLock (fun _ ->
            if closed then
                Async.result None
            else
                incomming.PostAndAsyncReply(ReadIncomming))

    member __.ReadOutgoing() =
        lock closeLock (fun _ ->
            if closed then
                Async.result None
            else
                outgoing.PostAndTryAsyncReply(ReadOutgoing, timeoutInMs))

    member __.AddOutgoing(messages) =
        outgoing.Post (NewOutgoing messages)

    member __.AddIncomming(msg) =
        incomming.Post (NewIncomming msg)

    member __.Close() =
        lock closeLock (fun _ ->
            if not closed then
                logVerbose "Closing"
                closed <- true
                comms.socketClosing id
                pingTimeoutTimer.Stop()
                pingTimeoutTimer.Dispose()
                incomming.Post CloseIncomming
                outgoing.Post CloseOutgoing
                logVerbose "Closed")
    
    member __.Send(messages) = this.AddOutgoing(messages |> List.map Message)
    member __.Send(messages) = this.AddOutgoing(messages |> Seq.map Message |> List.ofSeq)
    member __.Send(message) = this.AddOutgoing([Message message])
    member __.StartUpgrade(ws: WebSocket) = outgoing.Post (Upgrading ws)
    member __.FinishUpgrade() =
        transport <- Websocket
        outgoing.Post (Upgraded)

    interface IEngineIoSocket with
        member __.Id with get () = this.Id
        member __.Read() = this.ReadIncomming()
        member __.Send(messages: PacketContent seq) = this.Send(messages)
        member __.Send(message: PacketContent) = this.Send(message)
        member __.Broadcast(messages: PacketContent seq) = comms.broadcast (Some this.Id, messages)
        member __.Broadcast(message: PacketContent) = comms.broadcast (Some this.Id, ([message] :> _ seq))
        member __.Close() = this.Close()

let inline private badAsync err = Async.result (Bad [err]) |> AR
let inline private okAsync ok = Async.result (Ok(ok,[])) |> AR

type private RequestContext =
    {
        Transport: Transport option
        JsonPIndex: int option
        SocketId: string option
        SupportsBinary: bool
        IsBinary: bool
    }

let private getContext (req: HttpRequest): RequestContext =
    {
        Transport = req |> queryParam "transport" |> Option.bind(Transport.fromString)
        JsonPIndex = req |> queryParam "j" |> Option.bind Option.parseInt
        SocketId = req |> queryParam "sid"
        SupportsBinary = req |> queryParam "b64" |> Option.bind Option.parseIntAsBool |> Option.defaultArg false
        IsBinary = req |> header "content-type" = Some "application/octet-stream"
    }

/// Use CompareExchange to apply a mutation to a field.
/// Mutation must be pure & writes to the field should be rare compared to reads.
let private mutateField<'t when 't: not struct> (targetField: 't byref) (mutation: 't -> 't) =
    let mutable retry = true
    while retry do
        let before = targetField
        let newValue = mutation before
        let afterExchange = System.Threading.Interlocked.CompareExchange(&targetField, newValue, before)
        retry <- not (obj.ReferenceEquals(before, afterExchange))

type EngineIo(config, handleSocket: IEngineIoSocket -> Async<unit>) as this =
    let mutable sessions: Map<SocketId, EngineIoSocket> = Map.empty
    let idGenerator = Base64Id.create config.RandomNumberGenerator
    
    let socketTimeout = config.PingTimeout + config.PingInterval
    
    let tryGetSocket engineCtx =
        engineCtx.SocketId
        |> Option.map SocketId
        |> Option.bind (fun socketId -> Map.tryFind socketId sessions)

    let removeSocket (socketId: SocketId) =
        log.info (eventX "{socketId} Disconnected, removing session" >> Message.setFieldValue "socketId" socketId)
        mutateField &sessions (fun s -> s |> Map.remove socketId)

    let socketCommunications =
        {
            socketClosing = removeSocket
            broadcast = this.Broadcast
        }

    let payloadToResponse sid payload engineCtx =
        if engineCtx.SupportsBinary then
            bytesResponse HttpCode.HTTP_200 (payload |> PayloadEncoder.encodeToBinary |> Segment.toArray)
            |> setCookieSync' (mkCookie sid config)
            |> setUniqueHeader "Content-Type" "application/octet-stream"
            |> setContentBytes (payload |> PayloadEncoder.encodeToBinary |> Segment.toArray)
        else
            bytesResponse HttpCode.HTTP_200 (payload |> PayloadEncoder.encodeToString |> UTF8.bytes)
            |> setCookieSync' (mkCookie sid config)
            |> setUniqueHeader "Content-Type" "text/plain; charset=UTF-8"
            |> setContentBytes (payload |> PayloadEncoder.encodeToString |> System.Text.Encoding.UTF8.GetBytes)

    let handleGet' engineCtx: AsyncResult<SocketId*Payload, Error> =
        match engineCtx.SocketId with
        | None ->
            let socketIdString = idGenerator ()
            let socketId = SocketId socketIdString

            let socket = new EngineIoSocket(socketId, socketTimeout, socketCommunications, handleSocket)
            mutateField &sessions (fun s -> s |> Map.add socket.Id socket)
            socket.Start()
            
            log.info (eventX "{socketId} Connected, creating session" >> Message.setFieldValue "socketId" socketId)

            let handshake = mkHandshake socketIdString config
            let payload = Payload(Open(handshake) :: config.InitialPackets)
            okAsync (socketId, payload)
        | Some(sessionId) ->
            let socketId = SocketId sessionId
            match sessions |> Map.tryFind socketId with
            | Some(socket) -> asyncTrial {
                let! payload = socket.ReadOutgoing ()
                let payload' = defaultArg payload (Payload([]))
                log.verbose (eventX "{socketId} Http GET <- {message}"
                    >> Message.setFieldValue "socketId" sessionId
                    >> Message.setFieldValue "message"(Payload.getMessages payload'))

                return socketId, payload'
                }
            | None ->
                badAsync UnknownSessionId

    let resultToHttp (result:Result<HttpResult, Error>): HttpResult =
        match result with 
        | Ok(response, _) -> response
        | Bad(errors) ->
            let error = errors |> List.tryHead |> Option.orDefault Unknown
            match error with
            | Unknown -> simpleResponse HttpCode.HTTP_500 "Unknown error"
            | UnknownSessionId -> simpleResponse HttpCode.HTTP_404 "Unknown session ID"

    let handleGet engineCtx =
        asyncTrial {
            let! x = handleGet' engineCtx
            let (socketId, payload) = x
            return payloadToResponse socketId payload engineCtx
        }

    let handlePost' (req: HttpRequest) engineCtx: Result<SocketId, Error> =
        match tryGetSocket engineCtx with
        | Some(socket) ->
            let payload =
                if engineCtx.SupportsBinary then
                    req.rawForm |> Segment.ofArray |> PayloadDecoder.decodeFromBinary
                else
                    req.rawForm |> Text.Encoding.UTF8.GetString |> PayloadDecoder.decodeFromString
            for message in payload |> Payload.getMessages do
                log.verbose (eventX "{socketId} Http POST -> {message}"
                    >> Message.setFieldValue "socketId" socket.Id
                    >> Message.setFieldValue "message" message)
                socket.AddIncomming message
            ok socket.Id
        | None ->
            fail UnknownSessionId

    let handlePost engineCtx ctx =
        trial {
            let! socketId = handlePost' ctx engineCtx
            return simpleResponse HttpCode.HTTP_200 "" |> setCookieSync' (mkCookie socketId config)
        }

    let returnResponse ctx response =
        Some { ctx with response = response }

    let handleWebsocketCore (engineCtx: RequestContext) (webSocket : WebSocket) (context: HttpContext) (sessionId: string) = socket {
        let mutable loop = true

        while loop do
            let! msg = webSocket.read()

            match tryGetSocket engineCtx with
            | None ->
                loop <- false
            | Some socket -> 
                match msg with
                | (Text, data, true) ->
                    let str = UTF8.toString data
                    let packetMessage = PacketMessageDecoder.decodeFromString str
                    match socket.Transport with
                    | Websocket ->
                        log.verbose (eventX "{socketId} WebSocket -> {messages}"
                            >> Message.setFieldValue "socketId" sessionId
                            >> Message.setFieldValue "messages" packetMessage)

                        // If we're already in websocket mode we enqueue the received message
                        socket.AddIncomming(packetMessage)
                    | _ ->
                        // Otherwise we only handle a small subset of messages to allow browsers to detect that we
                        // support websocket correctly and ask for an upgrade
                        match packetMessage with
                        | Ping pingData when pingData = TextPacket "probe" ->
                            let answer = PacketMessageEncoder.encodeToString (Pong pingData) |> UTF8.bytes |> Segment.ofArray
                            do! webSocket.send Text answer true
                            socket.StartUpgrade(webSocket)
                        | Upgrade ->
                            socket.FinishUpgrade()
                        | _ -> ()
                | (Binary, data, true) ->
                    log.error (eventX "{socketId} Binary WebSocket data received, this case is not supported yet"
                        >> Message.setFieldValue "socketId" sessionId)
                    ()
                | (Opcode.Close, _, _) ->
                    log.verbose (eventX "{socketId} WebSocket Close received, closing socket"
                        >> Message.setFieldValue "socketId" sessionId)
                            
                    do! webSocket.send Opcode.Close Segment.empty true
                    socket.Close()
                    loop <- false
                | msg ->
                    log.warn (eventX "{socketId} Unhandled message received: {msg}"
                        >> Message.setFieldValue "socketId" sessionId
                        >> Message.setFieldValue "msg" msg)
                    ()
    }

    let handleWebsocket (engineCtx: RequestContext) (webSocket : WebSocket) (context: HttpContext) = async {
        match engineCtx.SocketId with
        | Some socketId ->
            let! coreResult = handleWebsocketCore engineCtx webSocket context socketId
        
            match coreResult with
            | Choice1Of2 _ -> ()
            | Choice2Of2 err ->
                match tryGetSocket engineCtx with
                | Some socket ->
                    log.verbose (eventX "{socketId} Error received on session WebSocket, closing: {error}"
                        >> Message.setFieldValue "socketId" socketId
                        >> setSocketErrorLogField "error" err)
                    socket.Close()
                | None -> ()
            return coreResult

        | None ->
            return! webSocket.send Opcode.Close Segment.empty true
    }

    let handle: WebPart = fun ctx ->
        let engineCtx = getContext ctx.request
        match ctx.request.``method``, engineCtx.Transport with
        | POST, Some(Polling) ->
            handlePost engineCtx ctx.request |> resultToHttp |> returnResponse ctx |> Async.result
        | GET, Some(Polling) ->
            let engineCtx = getContext ctx.request
            async {
                let! result = handleGet engineCtx |> Async.ofAsyncResult
                return returnResponse ctx (resultToHttp result)
            }
        | GET, Some(Websocket) ->
            handShake (handleWebsocket engineCtx) ctx
        | _ ->
            log.warn (eventX "Unknown request (method={method}, transport={transport})"
                >> setField "method" ctx.request.``method``
                >> setField "transport" engineCtx.Transport)
            Async.result None

    member val WebPart = handle

    /// Send the same packets to every session
    member __.Broadcast(exceptSocket: SocketId option, messages: PacketContent seq) =
        let messages' = List.ofSeq messages
        sessions |> Map.iter (fun socketId socket ->
            if Some socketId <> exceptSocket then
                socket.Send(messages'))

    /// Send the same packet to every session
    member __.Broadcast(exceptSocket: SocketId option, message: PacketContent) =
        sessions |> Map.iter (fun socketId socket ->
            if Some socketId <> exceptSocket then
                socket.Send(message))

// Fixed in next Suave version, https://github.com/SuaveIO/suave/pull/575
let private removeBuggyCorsHeader: WebPart =
    fun ctx -> async {
        let finalHeaders =
            ctx.response.headers
            |> List.filter (fun (k,v) -> k <> "Access-Control-Allow-Credentials" || v = "True")
            |> List.map(fun (k,v) -> if k = "Access-Control-Allow-Credentials" then k,v.ToLower() else k,v)
        return Some({ ctx with response = { ctx.response with headers = finalHeaders } })
    }

// Adapted from https://github.com/socketio/socket.io/pull/1333
let private disableXSSProtectionForIE: WebPart =
    warbler (fun ctx ->
        let ua = ctx |> Headers.getFirstHeader "User-Agent"
        match ua with
        | Some(ua) when ua.Contains(";MSIE") || ua.Contains("Trident/") ->
            Writers.setHeader "X-XSS-Protection" "0"
        | _ -> succeed
    )

let suaveEngineIo (engine: EngineIo) config: WebPart =
    let corsConfig =
        { defaultCORSConfig with
            allowedMethods = InclusiveOption.None
            allowedUris = InclusiveOption.All
            allowCookies = true }
    choose [
        Filters.pathStarts config.Path
            >=>
            choose [
                engine.WebPart
                RequestErrors.BAD_REQUEST "O_o"
            ]
            >=> cors corsConfig
            >=> removeBuggyCorsHeader
            >=> disableXSSProtectionForIE
    ]