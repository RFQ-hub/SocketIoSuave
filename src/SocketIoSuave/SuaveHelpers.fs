module private SocketIoSuave.SuaveHelpers

open Suave
open Suave.Cookie
open System
open Suave.Logging
open Suave.Sockets

let queryParam name (req: HttpRequest) =
    req.query
    |> List.tryFind (fun (key, _) -> key.Equals(name, StringComparison.InvariantCultureIgnoreCase))
    |> Option.bind snd

let header name (req: HttpRequest) =
    req.headers
    |> List.tryFind (fun (key, _) -> key.Equals(name, StringComparison.InvariantCultureIgnoreCase))
    |> Option.map snd

let setCookieSync (cookie : HttpCookie) (response: HttpResult) =
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

let setCookieSync' (cookie : HttpCookie option) (response: HttpResult) =
    match cookie with
    | Some cookie -> setCookieSync cookie response
    | None -> response

let inline setHeader name value response =
    let headers = (name, value)::response.headers
    { response with headers = headers }

let inline setUniqueHeader name value response =
    let headers = response.headers |> List.filter (fun (headerName, _) -> not (String.equalsOrdinalCI headerName name) )
    let headers = (name, value)::headers
    { response with headers = headers }

let inline setContentBytes bytes response =
    { response with content = HttpContent.Bytes bytes}

let inline bytesResponse (code: HttpCode) (bytes: byte[]) =
    { status = code.status; headers = []; content = Bytes bytes; writePreamble = true }

let inline simpleResponse (code: HttpCode) (message: string) =
    bytesResponse code (UTF8.bytes message)

/// Set the log field `name` with a textual description of the socket error
let setSocketErrorLogField name error =
    match error with
    | SocketError socketError -> Message.setFieldValue name (sprintf "%O" socketError)
    | InputDataError (code, message) ->
        let code = defaultArg code 400
        Message.setFieldValue name (sprintf "%i: %s" code message)
    | ConnectionError message -> Message.setFieldValue name message