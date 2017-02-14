module SocketIoSuave.EngineIo.Engine

open SocketIoSuave
open SocketIoSuave.EngineIo.Protocol
open System
open System.Security.Cryptography
open System.Collections.Generic
open Suave.Logging
open Suave.Logging.Message
open Suave
open Suave.CORS
open Suave.Operators
open Suave.Cookie
open Chessie.ErrorHandling

let ll = Targets.create Debug [| "Bug" |]

type SocketId = SocketId of string
with
    override x.ToString() = match x with | SocketId s -> s

type IncomingCommunication =
    | NewIncomming of PacketMessage
    | ReadIncomming of AsyncReplyChannel<PacketMessage>
    | CloseIncomming

type OutgoingCommunication =
    | NewOutgoing of PacketMessage
    | ReadOutgoing of AsyncReplyChannel<Payload>
    | CloseOutgoing

type EngineIoConfig =
    {
        Path: string
        Upgrades: string[] // "websocket"
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
            Upgrades = Array.empty

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

(*
let createSocket socketId handleIncomming app =
    let socket = {
        Id = socketId
        Transport = Polling
        IncomingMessages = 
        OutgoingMessages = 
    }

    socket.IncomingMessages.Post(IncomingCommunication.Init(socket))
    socket.OutgoingMessages.Post(OutgoingCommunication.Init(socket))
    socket
*)

let private mkHandshake socketId config =
    {
        Sid = socketId;
        Upgrades = config.Upgrades;
        PingTimeout = int config.PingTimeout.TotalMilliseconds;
        PingInterval = int config.PingInterval.TotalMilliseconds
    }

type Error =
    | UnknownSessionId
    | Unknown

type InternalEngineIoSocket(id: SocketId, pingTimeout: TimeSpan, onClose: InternalEngineIoSocket -> unit) as this =
    let logError s = ll.error (eventX (sprintf "[%s] %s" (id.ToString()) s))
    let logDebug s = ll.debug (eventX (sprintf "[%s] %s" (id.ToString()) s))
    let logWarn s = ll.warn (eventX (sprintf "[%s] %s" (id.ToString()) s))

    let closeLock = new obj()
    let mutable closed = false

    let timeoutInMs = int pingTimeout.TotalMilliseconds

    let pingTimeoutTimer =
        let timer = new System.Timers.Timer()
        timer.AutoReset <- false
        timer.Interval <- pingTimeout.TotalMilliseconds
        timer.Elapsed.Add(fun _ ->
            logDebug "Ping timeout, closing socket"
            this.Close())
        timer

    let setPingTimeout () =
        pingTimeoutTimer.Stop()
        pingTimeoutTimer.Start()

    let incomming = MailboxProcessor<IncomingCommunication>.Start(fun inbox ->
        let rec loop (messages: Queue<PacketMessage>) (currentReplyChan: AsyncReplyChannel<PacketMessage> option) = async {
            let! msg = inbox.Receive()

            match msg with
            | NewIncomming msg ->
                setPingTimeout()
                logDebug (sprintf "[NewIncomming] %A" msg)

                match msg with
                | Ping(data) ->
                    logDebug "[NewIncomming] Answering Ping with Pong"
                    this.AddOutgoing(Pong(data))
                | _ -> ()
                
                match currentReplyChan with
                | Some(chan) ->
                    logDebug (sprintf "[NewIncomming] Reply channel exists, sending %A" msg)
                    chan.Reply(msg)
                | None ->
                    messages.Enqueue msg

                return! loop messages None
            | ReadIncomming rep ->
                match messages.Count = 0, currentReplyChan with
                | true, None->
                    logDebug "[ReadIncomming] nothing yet"
                    return! loop messages (Some(rep))
                | false, None ->
                    logDebug (sprintf "[ReadIncomming] %i available" messages.Count)
                    let msg = messages.Dequeue()
                    rep.Reply msg
                    return! loop messages None
                | _, Some(_) ->
                    logWarn ""
                    // app.logError (sprintf "%A CROSS THE BEAMS" socket.Id)                           
                    failwith "Don't cross the beams !"
            | CloseIncomming ->
                logDebug "[CloseIncomming] Closing"
                return ()
        }

        loop (new Queue<PacketMessage>()) None
        )

    let outgoing = MailboxProcessor<OutgoingCommunication>.Start(fun inbox ->
        let rec loop messages (currentReplyChan: AsyncReplyChannel<Payload> option) = async {
            let! msg = inbox.Receive()
            match msg with
            | NewOutgoing msg ->
                match currentReplyChan with
                | Some(chan) ->
                    logDebug (sprintf "[NewOutgoing] Reply channel exists, sending %A" msg)
                    chan.Reply(Payload(msg::messages))
                    return! loop [] None
                | None ->
                    logDebug (sprintf "[NewOutgoing] Storing %A" msg)
                    return! loop (msg::messages) None
            | ReadOutgoing rep ->
                match currentReplyChan with
                | Some otherRep ->
                    // Two requests crossed, if the client behave normally the first one had a network error and
                    // timeouted, but just in case we still answer something
                    logWarn "[ReadOutgoing] Requests crossed, previous will get an empty payload"
                    otherRep.Reply(Payload([]))
                | None -> ()

                match messages with
                | []->
                    logDebug "[ReadOutgoing] nothing yet"
                    return! loop [] (Some(rep))
                | messages ->
                    logDebug (sprintf "[ReadOutgoing] %i available" messages.Length)
                    rep.Reply(Payload(messages))
                    return! loop [] None
            | CloseOutgoing ->
                logDebug "[CloseOutgoing] Closing"
                return ()
        }

        loop [] None
    )

    let onMailBoxError (x: Exception) =
        logError (x.ToString())
        this.Close()

    do
        incomming.DefaultTimeout <- timeoutInMs * 2
        incomming.Error.Add(onMailBoxError)
        outgoing.DefaultTimeout <- timeoutInMs * 2
        outgoing.Error.Add(onMailBoxError)
        setPingTimeout ()

    member val Id = id
    member __.ReadIncomming() =
        lock closeLock (fun _ ->
            if closed then
                Async.result None
            else
                incomming.PostAndTryAsyncReply(ReadIncomming, timeoutInMs))
    member __.ReadOutgoing() =
        lock closeLock (fun _ ->
            if closed then
                Async.result None
            else
                outgoing.PostAndTryAsyncReply(ReadOutgoing, timeoutInMs))
    member __.AddOutgoing(msg) = outgoing.Post (NewOutgoing msg)
    member __.AddIncomming(msg) = incomming.Post (NewIncomming msg)
    member __.Close() =
        lock closeLock (fun _ ->
            if not closed then
                closed <- true
                onClose this
                pingTimeoutTimer.Stop()
                pingTimeoutTimer.Dispose()
                incomming.Post CloseIncomming
                outgoing.Post CloseOutgoing)

type EngineIoSocket internal (socket: InternalEngineIoSocket) =
    member val Id = socket.Id
    member __.Read() = socket.ReadIncomming()
    member __.Send(msg) = socket.AddOutgoing(msg)
    member __.Close() = socket.Close()

let inline private badAsync err = Async.result (Bad [err]) |> AR
let inline private okAsync ok = Async.result (Ok(ok,[])) |> AR

type EngineApp = 
    {
        handleSocket: EngineIoSocket -> Async<unit>
    }

type private RequestContext =
    {
        Transport: string
        JsonPIndex: int option
        SessionId: string option
        SupportsBinary: bool
        IsBinary: bool
    }

let private queryParam name (req: HttpRequest) =
    req.query
    |> List.tryFind (fun (key, _) -> key.Equals(name, StringComparison.InvariantCultureIgnoreCase))
    |> Option.bind snd

let private header name (req: HttpRequest) =
    req.headers
    |> List.tryFind (fun (key, _) -> key.Equals(name, StringComparison.InvariantCultureIgnoreCase))
    |> Option.map snd

module private Option =
    let parseInt s =
        let ok, i = Int32.TryParse(s)
        if ok then Some i else None

    let parseIntAsBool s = parseInt s |> Option.map((<>) 0)

    let defaultArg default' opt = defaultArg opt default'

let private getContext (req: HttpRequest): RequestContext =
    {
        Transport = req |> queryParam "transport" |> Option.defaultArg "polling"
        JsonPIndex = req |> queryParam "j" |> Option.bind Option.parseInt
        SessionId = req |> queryParam "sid"
        SupportsBinary = req |> queryParam "b64" |> Option.bind Option.parseIntAsBool |> Option.defaultArg false
        IsBinary = req |> header "content-type" = Some "application/octet-stream"
    }

let private setCookieSync (cookie : HttpCookie) (response: HttpResult) =
    let notSetCookie : string * string -> bool =
        fst >> (String.equalsOrdinalCI Headers.Fields.Response.setCookie >> not)

    let cookieHeaders =
        response.cookies
        |> Map.put cookie.name cookie // possibly overwrite
        |> Map.toList
        |> List.map (snd >> HttpCookie.toHeader)

    let headers' =
      cookieHeaders
      |> List.fold (fun headers header ->
          (Headers.Fields.Response.setCookie, header) :: headers)
          (response.headers |> List.filter notSetCookie)

    { response with headers = headers' }

let private setCookieSync' (cookie : HttpCookie option) (response: HttpResult) =
    match cookie with
    | Some cookie -> setCookieSync cookie response
    | None -> response

let inline private setHeader name value response =
    let headers = (name, value)::response.headers
    { response with headers = headers }

let inline private setUniqueHeader name value response =
    let headers = response.headers |> List.filter (fun (headerName, _) -> not (String.equalsOrdinalCI headerName name) )
    let headers = (name, value)::headers
    { response with headers = headers }

let inline private setContentBytes bytes response =
    { response with content = HttpContent.Bytes bytes}

let inline private bytesResponse (code: HttpCode) (bytes: byte[]) =
    { status = code.status; headers = []; content = Bytes bytes; writePreamble = true }

let inline private simpleResponse (code: HttpCode) (message: string) =
    bytesResponse code (UTF8.bytes message)

/// Use CompareExchange to apply a mutation to a field.
/// Mutation must be pure & writes to the field should be rare compared to reads.
let mutateField<'t when 't: not struct> (targetField: 't byref) (mutation: 't -> 't) =
    let mutable retry = true
    while retry do
        let before = targetField
        let newValue = mutation before
        let afterExchange = System.Threading.Interlocked.CompareExchange(&targetField, newValue, before)
        retry <- not (obj.ReferenceEquals(before, afterExchange))

type EngineIo(config, app: EngineApp) =
    let mutable sessions: Map<SocketId, InternalEngineIoSocket> = Map.empty
    let idGenerator = Base64Id.create config.RandomNumberGenerator
    
    let socketTimeout = config.PingTimeout + config.PingInterval
    
    let socketClosed (socket: InternalEngineIoSocket) =
        ll.info (eventX (sprintf "Removing session with ID %s" (socket.Id.ToString())))
        mutateField &sessions (fun s -> s |> Map.remove socket.Id)

    let payloadToResponse sid payload engineCtx =
        if engineCtx.SupportsBinary then
            bytesResponse HttpCode.HTTP_200 (payload |> Payload.encodeToBinary |> Segment.toArray)
            |> setCookieSync' (mkCookie sid config)
            |> setUniqueHeader "Content-Type" "application/octet-stream"
            |> setContentBytes (payload |> Payload.encodeToBinary |> Segment.toArray)
        else
            bytesResponse HttpCode.HTTP_200 (payload |> Payload.encodeToString |> UTF8.bytes)
            |> setCookieSync' (mkCookie sid config)
            |> setUniqueHeader "Content-Type" "text/plain; charset=UTF-8"
            |> setContentBytes (payload |> Payload.encodeToString |> System.Text.Encoding.UTF8.GetBytes)

    let handleGet' engineCtx: AsyncResult<SocketId*Payload, Error> =
        match engineCtx.SessionId with
        | None ->
            let socketIdString = idGenerator ()
            let socketId = SocketId socketIdString

            let socket = new InternalEngineIoSocket(socketId, socketTimeout, socketClosed)
            mutateField &sessions (fun s -> s |> Map.add socket.Id socket)
            
            ll.info (eventX (sprintf "Creating session with ID %s" socketIdString))

            // TODO: When the handler finishes we want to kill the session
            app.handleSocket (new EngineIoSocket(socket)) |> Async.StartAsTask |> ignore

            let handshake = mkHandshake socketIdString config
            let payload = Payload(Open(handshake) :: config.InitialPackets)
            okAsync (socketId, payload)
        | Some(sessionId) ->
            let socketId = SocketId sessionId
            match sessions |> Map.tryFind socketId with
            | Some(socket) -> asyncTrial {
                let! payload = socket.ReadOutgoing ()
                let payload' = defaultArg payload (Payload([]))
                return socketId, payload'
                }
            | None ->
                badAsync UnknownSessionId

    let errorsToHttp (result:Result<HttpResult, Error>): HttpResult =
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
        match engineCtx.SessionId with
        | None -> fail UnknownSessionId
        | Some(sessionId) -> 
            let socketId = SocketId sessionId
            match sessions |> Map.tryFind socketId with
            | Some(socket) ->
                let payload =
                    if engineCtx.SupportsBinary then
                        req.rawForm |> Segment.ofArray |> Payload.decodeFromBinary
                    else
                        req.rawForm |> Text.Encoding.UTF8.GetString |> Payload.decodeFromString
                for message in payload |> Payload.getMessages do
                    ll.debug (eventX (sprintf "%s -> %A" sessionId message))
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

    let handle: WebPart = fun ctx ->
        let engineCtx = getContext ctx.request

        match ctx.request.``method`` with
        | POST -> handlePost engineCtx ctx.request |> errorsToHttp |> returnResponse ctx |> Async.result
        | GET -> async {
            let! result = handleGet engineCtx |> Async.ofAsyncResult
            return returnResponse ctx (errorsToHttp result)
            }
        | _ -> Async.result None

    member val Handle = handle


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

let suaveEngineIo config handleSocket: WebPart =
    let engine = new EngineIo(config, { handleSocket = handleSocket } )
    let corsConfig =
        { defaultCORSConfig with
            allowedMethods = InclusiveOption.None
            allowedUris = InclusiveOption.All
            allowCookies = true }
    choose [
        Filters.pathStarts config.Path
            >=>
            choose [
                engine.Handle
                RequestErrors.BAD_REQUEST "O_o"
            ]
            >=> cors corsConfig
            >=> removeBuggyCorsHeader
            >=> disableXSSProtectionForIE
    ]