﻿namespace Logary

open System
open System.Globalization
open System.Text
open System.IO
open Microsoft.FSharp.Reflection
open NodaTime
open Logary
open Logary.Internals.FsMessageTemplates


/// A thing that efficiently writes a message to a TextWriter.
type MessageWriter =
  abstract write : TextWriter -> Message -> unit

[<AutoOpen>]
module MessageWriterEx =
  type MessageWriter with
    [<Obsolete "Try to write directly to a System.IO.TextWriter instead">]
    member x.format (m : Message) =
      use sw = StringWriter()
      x.write sw m
      sw.ToString()

module internal MessageParts =
  open Message

  /// Returns the case name of the object with union type 'ty.
  let caseNameOf (x:'a) =
    match FSharpValue.GetUnionFields(x, typeof<'a>) with
    | case, _ -> case.Name

  /// Format a timestamp in nanoseconds since epoch into a ISO8601 string
  let formatTimestamp (ticks : int64) =
    Instant.FromTicksSinceUnixEpoch(ticks)
      .ToDateTimeOffset()
      .ToString("o", CultureInfo.InvariantCulture)

  let getTextWriter (provider : IFormatProvider) nl =
    let tw = new StringWriter ()
    tw.NewLine <- nl
    tw

  let formatWithProvider (provider : IFormatProvider) (arg : obj) format =
    let customFormatter = provider.GetFormat(typeof<System.ICustomFormatter>) :?> System.ICustomFormatter
    match customFormatter with
    | cf when not (isNull cf) ->
      (cf.Format(format, arg, provider))
    | _ ->
      match arg with
      | :? System.IFormattable as f -> f.ToString(format, provider)
      | _ -> arg.ToString()

  let generateFormattedTemplateByGauges (tw: TextWriter) (gauges : List<string * Gauge>) =
    if gauges.Length = 0 then String.Empty
    else
      tw.Write "Gauges: ["

      let lastIndex = gauges.Length - 1
      gauges
      |> List.iteri (fun i (gaugeType, Gauge (value, units)) ->
         let valueFormat = formatWithProvider tw.FormatProvider value null
         let unitsFormat = Units.symbol units
         tw.Write gaugeType
         tw.Write " : "
         tw.Write valueFormat
         if not <| String.IsNullOrEmpty unitsFormat then tw.Write (" " + unitsFormat)
         if i <> lastIndex then tw.Write ", ")

      tw.Write "]"
      tw.ToString ()

  let generateFormattedTemplateByFields (tw: TextWriter) (template : Template)
                                        (destr : Destructurer) maxDepth (fields : seq<string * obj>) =
    if Seq.isEmpty fields then template.FormatString
    else
      let fieldsMap = fields |> HashMap.ofSeq
      let propertiesMap = template.Properties |> Seq.map (fun p -> (p.Name, p.Destr)) |> HashMap.ofSeq
      let getValueByName name =
        match (HashMap.tryFind name fieldsMap, HashMap.tryFind name propertiesMap) with
        | Some (value), Some (destrHint) ->
          destr (DestructureRequest(destr, value, maxDepth, 1, hint=destrHint))
        | _ -> TemplatePropertyValue.Empty
      Formatting.formatCustom template tw getValueByName
      tw.ToString ()

  let formatTemplate provider nl destr maxDepth message =
    let (Event (formatTemplate)) = message.value
    if String.IsNullOrEmpty formatTemplate then
      if hasGauge message then
        use tw = getTextWriter provider nl
        message |> getAllGauges |> List.ofSeq |> generateFormattedTemplateByGauges tw
      else
        String.Empty
    else
      let parsedTemplate = Parser.parse (formatTemplate)
      if parsedTemplate.HasAnyProperties then
        use tw = getTextWriter provider nl
        message |> getAllFields |> generateFormattedTemplateByFields tw parsedTemplate destr maxDepth
      else
        // raw message with no fields needs format
        formatTemplate

  let rec writePropValue (w: TextWriter) (tpv: TemplatePropertyValue) depth =
    // context: use 2 indent, fields/gauges/other use 2 indent, depth start from 0, so 2+2+2 = 6
    let indent = new String (' ', depth * 2 + 6)
    match tpv with
    | ScalarValue sv ->
      match sv with
      | null -> w.Write "null"
      | :? string as s ->
        w.Write "\""
        w.Write (s.Replace("\"", "\\\""))
        w.Write "\""
      | _ ->
        let customFormatter = w.FormatProvider.GetFormat(typeof<System.ICustomFormatter>) :?> System.ICustomFormatter
        match customFormatter with
        | cf when not (isNull cf) ->
          w.Write (cf.Format(null, sv, w.FormatProvider))
        | _ ->
          match sv with
          | :? System.IFormattable as f -> w.Write (f.ToString(null, w.FormatProvider))
          | _ -> w.Write(sv.ToString())

    | SequenceValue svs ->
      svs
      |> List.iter (fun sv ->
         w.WriteLine (); w.Write indent; w.Write "- "; writePropValue w sv (depth + 1);)

    | StructureValue(typeTag, values) ->
      let writeTypeTag = not <| isNull typeTag
      if writeTypeTag then w.WriteLine (); w.Write indent; w.Write typeTag; w.Write " {";

      values
      |> List.iter (fun nv ->
         w.WriteLine (); w.Write indent; w.Write nv.Name; w.Write " => "; writePropValue w nv.Value (depth + 1);)

      if writeTypeTag then w.WriteLine (); w.Write indent; w.Write "}";

    | DictionaryValue(kvList) ->
      kvList
      |> List.iter (fun (entryKey, entryValue) ->
         match entryKey with
         | ScalarValue _ ->
           w.WriteLine (); w.Write indent; writePropValue w entryKey (depth + 1); w.Write " => "; writePropValue w entryValue (depth + 1);
         | _ ->
           // default case will not go to here, unless user define their own DictionaryValue which its entryKey is not ScalarValue
           w.WriteLine (); w.Write indent; w.Write "- key => "; writePropValue w entryKey (depth + 1);
           w.WriteLine (); w.Write indent; w.Write "  value => "; writePropValue w entryValue (depth + 1);)
    ()

  let formatContext (formatProvider: IFormatProvider) (nl: string) (destr: Destructurer) maxDepth message =
    let padding = new String (' ', 6)

    let inline appendWithNl (sb: StringBuilder) (prefix: string) (value: string) (nl: string) =
      if not <| String.IsNullOrEmpty value then sb.Append(prefix).Append(value).Append(nl) |> ignore

    let inline processKvs sb (prefix: string) (nl: string) (kvs: seq<string * obj>) =
      use tw = getTextWriter formatProvider nl
      kvs
      |> Seq.iter (fun (name, value) ->
         let destrValue = destr (DestructureRequest(destr, value, maxDepth, 1, hint=DestrHint.Destructure))
         tw.Write nl; tw.Write padding; tw.Write name; tw.Write " => "; writePropValue tw destrValue 1)

      appendWithNl sb prefix (tw.ToString()) nl

    let sb = StringBuilder ()

    // process fields
    let (Event (formatTemplate)) = message.value
    if not <| String.IsNullOrEmpty formatTemplate then
      let parsedTemplate = Parser.parse (formatTemplate)
      if parsedTemplate.HasAnyProperties then
        use tw = getTextWriter formatProvider nl

        let fieldsHap = message |> getAllFields |> HashMap.ofSeq
        parsedTemplate.Tokens
        |> Seq.iter (fun token ->
           match token with
           | TextToken _ -> ()
           | PropToken (_, prop) as fieldToken ->
             match fieldsHap |> HashMap.tryFind prop.Name with
             | None -> ()
             | Some fieldValue ->
               tw.Write nl; tw.Write padding; tw.Write prop.Name; tw.Write " => ";
               let buffer = StringBuilder ()
               let destrValue = destr (DestructureRequest(destr, fieldValue, maxDepth, 1, hint= prop.Destr))
               Formatting.writeToken buffer tw fieldToken destrValue)

        appendWithNl sb "    fields:" (tw.ToString()) nl
      else ()
    else ()

    // process gauge
    message |> getAllGauges |> Seq.map (fun (k, gauge) -> (k, box gauge)) |> processKvs sb "    gauges:" nl

    // process others
    message |> GetContextsOtherThanGaugeAndFields |> processKvs sb "    others:" nl

    let wholeRes = sb.ToString ()
    if String.IsNullOrEmpty wholeRes then String.Empty
    else String.Concat(nl,"  context:", nl, wholeRes, nl)

  let rec formatValueLeafs (ns : string list) (value : Value) =
    let rns = lazy (PointName.ofList (List.rev ns))
    seq {
      match value with
      | String s ->
        yield rns.Value, s
      | Bool b ->
        yield rns.Value, b.ToString()
      | Float f ->
        yield rns.Value, f.ToString()
      | Int64 i ->
        yield rns.Value, i.ToString ()
      | BigInt b ->
        yield rns.Value, b.ToString ()
      | Binary (b, _) ->
        yield rns.Value, BitConverter.ToString b |> fun s -> s.Replace("-", "")
      | Fraction (n, d) ->
        yield rns.Value, sprintf "%d/%d" n d
      | Array list ->
        for item in list do
          yield! formatValueLeafs ns item
      | Object m ->
        for KeyValue (k, v) in m do
          yield! formatValueLeafs (k :: ns) v
    }

/// simple message writer use messagetemplates
/// json writer should use from other project that use fspickler.json
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module MessageWriter =
  open MessageParts

  let private defaultDestr = Capturing.createCustomDestructurer None (Some Capturing.builtInFSharpTypesDestructurer)

  /// maxDepth can be avoided if cycle reference are handled properly
  let expanded destr maxDepth nl ending : MessageWriter =
    { new MessageWriter with
        member x.write tw m =
          let level = string (caseNameOf m.level).[0]
          // https://noda-time.googlecode.com/hg/docs/api/html/M_NodaTime_OffsetDateTime_ToString.htm
          let time = formatTimestamp m.timestampTicks
          let body = formatTemplate tw.FormatProvider nl destr maxDepth m
          let name = m.name.ToString()
          let context = formatContext tw.FormatProvider nl destr maxDepth m
          sprintf "%s %s: %s [%s]%s%s" level time body name context ending
          |> tw.Write
    }

  /// Verbatim simply outputs the message and no other information
  /// and doesn't append a newline to the string.
  let verbatim =
    { new MessageWriter with
        member x.write tw m =
          formatTemplate tw.FormatProvider Environment.NewLine defaultDestr 10 m
          |> tw.Write
    }

  /// VerbatimNewline simply outputs the message and no other information
  /// and does append a newline to the string.
  let verbatimNewLine =
    { new MessageWriter with
        member x.write tw m =
          verbatim.write tw m
          tw.WriteLine()
    }

  /// <see cref="MessageWriter.LevelDatetimePathMessageNewLine" />
  let levelDatetimeMessagePath =
    expanded defaultDestr 10 Environment.NewLine ""

  /// LevelDatetimePathMessageNl outputs the most information of the Message
  /// in text format, starting with the level as a single character,
  /// then the ISO8601 format of a DateTime (with +00:00 to show UTC time),
  /// then the path in square brackets: [Path.Here], the message and a newline.
  /// Exceptions are called ToString() on and prints each line of the stack trace
  /// newline separated.
  let levelDatetimeMessagePathNewLine =
    expanded defaultDestr 10 Environment.NewLine Environment.NewLine
