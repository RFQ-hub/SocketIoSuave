module SuaveEngine

(*
open SocketIoSuave
open SocketIoSuave.EngineIo.Engine
open Suave
open Suave.Cookie
open Suave.CORS
open Suave.Operators
open Suave.Logging
open SocketIoSuave.EngineIo.Protocol
open Suave.Logging.Message
open System

type SuaveEngineIoConfig =
    {
        EngineConfig: EngineIoConfig
        CORSConfig: CORSConfig
    }

    with static member empty = {
            EngineConfig = EngineIoConfig.empty
            CORSConfig =
                { defaultCORSConfig with
                    allowedMethods = InclusiveOption.None
                    allowedUris = InclusiveOption.All
                    allowCookies = true }
        }

let getContext (ctx: HttpContext) : EngineIoContext =
    {
        Transport = ctx.request.queryParam "transport" |> Choice.defaultArg "polling"
        JsonPIndex = ctx.request.queryParam "j" |> Choice.bind Choice.parseInt |> Option.ofChoice
        SessionId = ctx.request.queryParam "sid" |> Option.ofChoice
        SupportsBinary = ctx.request.queryParam "b64" |> Choice.bind Choice.parseIntAsBool |> Choice.defaultArg false
        IsBinary = defaultArg (ctx |> Headers.getFirstHeader "content-type") "" = "application/octet-stream"
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

let private setCookieSync (cookie: CookieDescription) (ctx: HttpContext) =
    setCookieSync { HttpCookie.empty with name = cookie.name; value = cookie.value; path = cookie.path; httpOnly = cookie.httpOnly } ctx


let ll = Targets.create Debug [| "Bug" |]

let suaveEngineApp: EngineApp<HttpContext> =
    {
        getQueryParams = getContext
        getContent = fun ctx -> ctx.request.rawForm
        logInfo = fun s -> ll.info (eventX s)
        logDebug = fun s -> ll.debug (eventX s)
        logError = fun s -> ll.error (eventX s)
    }

let serveEngineIo (config: EngineIoConfig) =
    runEngine config suaveEngineApp
    let respondPayload payload engineContext: WebPart = 
        if engineContext.SupportsBinary then
            Writers.setHeader "Content-Type" "text/plain; charset=UTF-8"
                >=> Successful.OK (payload |> Payload.encodeToString)
        else
            Writers.setHeader "Content-Type" "application/octet-stream"
                >=> Successful.ok (payload |> Payload.encodeToBinary |> Segment.toArray)

    let getPart: WebPart = fun ctx -> async {
        let engineContext = getContext ctx
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
        let engineContext = getContext ctx
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
*)