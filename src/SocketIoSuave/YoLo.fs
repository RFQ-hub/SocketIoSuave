[<AutoOpen>]
module YoLo

// Subset of https://github.com/haf/YoLo/blob/master/YoLo.fs (License: WTFPL)

open System

module String =

  let equalsOrdinalCI (str1 : string) (str2 : string) =
    String.Equals(str1, str2, StringComparison.OrdinalIgnoreCase)

module Choice =
    let map f = function
        | Choice1Of2 v -> Choice1Of2 (f v)
        | Choice2Of2 msg -> Choice2Of2 msg

module Async =
    let result = async.Return

    let map f value = async {
        let! v = value
        return f v
    }

module Option =
    let orDefault value = function
        | Some v -> v
        | _ -> value

module UTF8 =
    open System.Text

    let private utf8 = Encoding.UTF8

    /// Convert the full buffer `b` filled with UTF8-encoded strings into a CLR
    /// string.
    let toString (bs : byte []) =
        utf8.GetString bs

    /// Get the UTF8-encoding of the string.
    let bytes (s : string) =
        utf8.GetBytes s
