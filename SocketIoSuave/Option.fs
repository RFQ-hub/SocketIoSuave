module internal Option

open System

let parseInt s =
    let ok, i = Int32.TryParse(s)
    if ok then Some i else None

let parseIntAsBool s = parseInt s |> Option.map((<>) 0)

let defaultArg default' opt = defaultArg opt default'

let flattern =
    function
    | Some(Some(value)) -> Some value
    | _ -> None