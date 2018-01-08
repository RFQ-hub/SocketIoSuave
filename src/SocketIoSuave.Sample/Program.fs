open System
open Suave
open Suave.CORS
open Suave.Operators
open SocketIoSuave.EngineIo.Protocol
open SocketIoSuave
open System.Security.Cryptography
open Suave.Filters
open Suave.Logging
open Suave.Logging.Message
open SocketIoSuave.EngineIo
open SocketIoSuave.SocketIo
open SocketIoSuave.SocketIo.Engine
open System.Collections.Generic
open System.Threading.Tasks
open System.IO
open Suave.Files
open System.Threading
open Newtonsoft.Json
open Newtonsoft.Json.Linq

let private log = Targets.create Verbose [| "SocketIoSuave" |]

type NewMessageEvent = {
    username: string
    message: string
}

type LoginResponse = {
    numUsers: int
}

type UserJoinedEvent = {
    username: string
    numUsers: int
}

type TypingEvent = {
    username: string
}

type State = {
    userName: string option
}
with
    static member empty = { State.userName = None }

[<EntryPoint>]
let main argv = 
    let socketio handlePackets = SocketIo(SocketIoConfig.empty, handlePackets).WebPart

    let mutable userCount = 0

    let rec handlePacket state (socket: ISocketIoSocket) httpContext = async {
        let logVerbose s = log.debug (eventX (sprintf "{socketId} %s" s) >> setField "socketId" (socket.Id))
        let! p = socket.Receive()
        match p with
        | Some packet ->
            match packet.EventId with
            | Some id -> socket.SendPacket({ Packet.ofType Ack with EventId = Some id })
            | None -> ()
            let cmd = packet.Data.[0].ToObject<string>()
            let newState =
                match cmd with
                | "new message" ->
                    let textMessage = packet.Data.[1].ToObject<string>()
                    let userName = defaultArg state.userName "???"
                    logVerbose (sprintf "[%s] %s" userName textMessage)
                    socket.Broadcast ("new message", [{ username = userName; message = textMessage }])
                    state
                | "add user" ->
                    match state.userName with
                    | Some _ -> state
                    | None ->
                        let userName = packet.Data.[1].ToObject<string>()
                        Interlocked.Increment(&userCount) |> ignore
                        logVerbose (sprintf "[%A] JOIN" userName)
                        socket.Broadcast ("user joined", [{ UserJoinedEvent.username = userName; numUsers = userCount }])
                        socket.Send ("login", [{ UserJoinedEvent.username = userName; numUsers = userCount }])
                        { state with userName = Some userName }
                | "typing" ->
                    socket.Broadcast ("typing", [{ TypingEvent.username = defaultArg state.userName "???" }])
                    state
                | "stop typing" ->
                    socket.Broadcast ("stop typing", [{ TypingEvent.username = defaultArg state.userName "???" }])
                    state
                | other ->
                    logVerbose (sprintf "Unknown command: %s" cmd)
                    state

            return! handlePacket newState socket httpContext
        | None ->
            let userName = defaultArg state.userName "???"
            logVerbose (sprintf "[%A] LEAVE" userName)
            match state.userName with
            | Some userName ->
                Interlocked.Decrement(&userCount) |> ignore
                socket.Broadcast ("user left", [{ UserJoinedEvent.username = userName; numUsers = userCount }])
            | None -> ()
            return ()
    }

    let app =
        choose [
            socketio (handlePacket State.empty)
            GET >=> choose [
                browseHome
            
                path "/" >=> browseFileHome "index.html"
            ]

            RequestErrors.NOT_FOUND "File not found"
        ]

    let assemblyPath = Uri(System.Reflection.Assembly.GetExecutingAssembly().GetName().CodeBase).LocalPath
    let publicPath = Path.Combine(Path.GetDirectoryName(assemblyPath), "public")
    printfn "Will serve files from %s" publicPath
    let suaveConf = { defaultConfig with logger = log; homeFolder = Some publicPath }
    startWebServer suaveConf app
    0

