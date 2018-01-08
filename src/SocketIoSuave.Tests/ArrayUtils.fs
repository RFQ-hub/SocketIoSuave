module ArrayUtils

open System.Linq

/// Ordinally compare two arrays in constant time, bounded by the length of the
/// longest array. This function uses the F# language equality.
let equals (arr1 : 'a []) (arr2 : 'a []) =
    if arr1.Length <> arr2.Length then false else
    arr1.SequenceEqual(arr2)
