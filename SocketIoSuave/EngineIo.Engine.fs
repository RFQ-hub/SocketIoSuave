module SocketIoSuave.EngineIo.Engine
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