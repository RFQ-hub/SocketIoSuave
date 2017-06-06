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

let private log = Targets.create Debug [| "SocketIoSuave" |]

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

let chatPacket<'t> (cmd: string) (data: 't) =
    {
        Packet.ofType Event with
            Data = [ JToken.FromObject(cmd); JToken.FromObject(box data) ]
    }

[<EntryPoint>]
let main argv = 
    let socketio handlePackets = SocketIo(handlePackets).WebPart

    let mutable userCount = 0

    let rec handlePacket state (socket: ISocketIoSocket) = async {
        let logVerbose s = log.debug (eventX (sprintf "program {socketId}: %s" s) >> setField "socketId" (id.ToString()))
        let! p = socket.Receive()
        match p with
        | Some packet ->
            log.info (eventX (sprintf "< %A" packet))
            match packet.EventId with
            | Some id -> socket.Send({ Packet.ofType Ack with EventId = Some id })
            | None -> ()
            let cmd = packet.Data.[0].ToObject<string>()
            let newState =
                match cmd with
                | "new message" ->
                    let textMessage = packet.Data.[1].ToObject<string>()
                    logVerbose (sprintf "Received %s" textMessage)
                    socket.Broadcast (chatPacket "new message" { username = defaultArg state.userName ""; message = textMessage })
                    state
                | "add user" ->
                    match state.userName with
                    | Some _ -> state
                    | None ->
                        let userName = packet.Data.[1].ToObject<string>()
                        Interlocked.Increment(&userCount) |> ignore
                        socket.Broadcast (chatPacket "user joined" { UserJoinedEvent.username = userName; numUsers = userCount })
                        socket.Send (chatPacket "login" { UserJoinedEvent.username = userName; numUsers = userCount })
                        { state with userName = Some userName }
                | "typing" ->
                    socket.Broadcast (chatPacket "typing" { TypingEvent.username = defaultArg state.userName "???" })
                    state
                | "stop typing" ->
                    socket.Broadcast (chatPacket "stop typing" { TypingEvent.username = defaultArg state.userName "???" })
                    state
                | other ->
                    logVerbose (sprintf "Unknown command: %s" cmd)
                    state

            logVerbose "Let's loop"
            return! handlePacket newState socket
        | None ->
            logVerbose "None received, it's the end !"
            match state.userName with
            | Some userName ->
                Interlocked.Decrement(&userCount) |> ignore
                socket.Broadcast { Packet.ofType Event with Data = [ JToken.FromObject("user left"); JToken.FromObject({ UserJoinedEvent.username = userName; numUsers = userCount })] }
            | None -> ()
            return ()
    }

    let app =
        choose [
            //serveEngineIo { EngineIoConfig.empty with Path = "/socket.io/"; InitialPackets = initialPackets }
            socketio (handlePacket State.empty)
            // serveSocketIo emptySocketIoConfig
            GET >=> choose [
                browseHome
            
                path "/" >=> browseFileHome "index.html"
            ]

            RequestErrors.NOT_FOUND "File not found"
        ]

    let assemblyPath = Uri(System.Reflection.Assembly.GetExecutingAssembly().GetName().CodeBase).LocalPath
    let publicPath = Path.Combine(Path.GetDirectoryName(assemblyPath), "public")
    let suaveConf = { defaultConfig with logger = log; homeFolder = Some publicPath }
    startWebServer suaveConf app
    0

