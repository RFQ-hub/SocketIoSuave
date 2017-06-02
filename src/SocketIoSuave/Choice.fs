module SocketIoSuave.Choice

open System

/// Return the value if it's a Choice1Of2 or default' otherwise
let defaultArg default' = function | Choice1Of2 x -> x | Choice2Of2 _ -> default'

let parseInt s =
    let ok, i = Int32.TryParse(s)
    if ok then Choice1Of2 i else Choice2Of2 (sprintf "Not a number: '%s'" s)

let parseIntAsBool s = parseInt s |> Choice.map((<>) 0)