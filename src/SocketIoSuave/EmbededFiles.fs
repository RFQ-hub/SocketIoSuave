/// 'socket.io.js' file is embeded in the dll and can be served directly from there
module private SocketIoSuave.EmbededFiles

open Suave
open Suave.Operators
open System.IO
open System.Reflection

let private getResource name = 
    use stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(sprintf "SocketIoSuave.%s" name);
    use memStream = new MemoryStream(int stream.Length)
    stream.CopyTo(memStream)
    memStream.ToArray()
    
type private Resource = { fileName: string; mimeType: string; bytes: Lazy<byte[]> }

let private resources =
    [
        "socket.io.js", "application/javascript"
        "socket.io.js.map", "application/json"
    ]
    |> List.map(fun (fileName, mimeType) ->
        {
            fileName = fileName
            mimeType = mimeType
            bytes = lazy (getResource fileName)
        }
    )

let private sendResource res: WebPart = fun ctx ->
    {
        ctx with response = { ctx.response with content = Bytes res.bytes.Value; status = HTTP_200.status }
    } |> Some |> async.Return

/// Serve the embeded socket.io js client in the specified basePath
let handleInPath (basePath: string) =
    let basePath = if basePath.EndsWith("/") then basePath else basePath + "/"
    let resourceParts =
        resources
        |> List.map(fun res ->
            Filters.path (basePath + res.fileName)
            >=> Writers.setMimeType res.mimeType
            >=> sendResource res)

    choose resourceParts
