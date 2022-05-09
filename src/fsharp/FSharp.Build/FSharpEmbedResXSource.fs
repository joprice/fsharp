// Copyright (c) Microsoft Corporation.  All Rights Reserved.  See License.txt in the project root for license information.

namespace FSharp.Build

open System
open System.Collections
open System.Globalization
open System.IO
open System.Linq
open System.Text
open System.Xml.Linq
open Microsoft.Build.Framework
open Microsoft.Build.Utilities

type FSharpEmbedResXSource() =
    let mutable _buildEngine: IBuildEngine MaybeNull = null
    let mutable _hostObject: ITaskHost MaybeNull = null
    let mutable _embeddedText: ITaskItem[] = [||]
    let mutable _generatedSource: ITaskItem[] = [||]
    let mutable _outputPath: string = ""
    let mutable _targetFramework: string = ""

    let boilerplate = @"// <auto-generated>

namespace {0}

open System.Reflection

module internal {1} =
    type private C (_dummy:System.Int32) = class end
    let mutable Culture = System.Globalization.CultureInfo.CurrentUICulture
    let ResourceManager = new System.Resources.ResourceManager(""{2}"", C(0).GetType().GetTypeInfo().Assembly)
    let GetString(name:System.String) : System.String = ResourceManager.GetString(name, Culture)"

    let boilerplateGetObject = "    let GetObject(name:System.String) : System.Object = ResourceManager.GetObject(name, Culture)"

    let generateSource (resx:string) (fullModuleName:string) (generateLegacy:bool) (generateLiteral:bool) =
        try
            let printMessage = printfn "FSharpEmbedResXSource: %s"
            let justFileName = Path.GetFileNameWithoutExtension(resx)
            let sourcePath = Path.Combine(_outputPath, justFileName + ".fs")

            // simple up-to-date check
            if File.Exists(resx) && File.Exists(sourcePath) &&
                File.GetLastWriteTimeUtc(resx) <= File.GetLastWriteTimeUtc(sourcePath) then
                printMessage (sprintf "Skipping generation: '%s' since it is up-to-date." sourcePath)
                Some(sourcePath)
            else
                let namespaceName, moduleName =
                    let parts = fullModuleName.Split('.')
                    if parts.Length = 1 then ("global", parts.[0])
                    else (String.Join(".", parts, 0, parts.Length - 1), parts.[parts.Length - 1])
                let generateGetObject = not (_targetFramework.StartsWith("netstandard1.") || _targetFramework.StartsWith("netcoreapp1."))
                printMessage (sprintf "Generating code for target framework %s" _targetFramework)
                let sb = StringBuilder().AppendLine(String.Format(boilerplate, namespaceName, moduleName, justFileName))
                if generateGetObject then sb.AppendLine(boilerplateGetObject) |> ignore
                printMessage <| sprintf "Generating: %s" sourcePath
                let body =
                    let xname = XName.op_Implicit
                    XDocument.Load(resx).Descendants(xname "data")
                    |> Seq.fold (fun (sb:StringBuilder) (node:XElement) ->
                        let name =
                            match node.Attribute(xname "name") with
                            | null -> failwith (sprintf "Missing resource name on element '%s'" (node.ToString()))
                            | attr -> attr.Value
                        let docComment =
                            match node.Elements(xname "value").FirstOrDefault() with
                            | null -> failwith <| sprintf "Missing resource value for '%s'" name
                            | element -> element.Value.Trim()
                        let identifier = if Char.IsLetter(name.[0]) || name.[0] = '_' then name else "_" + name
                        let commentBody =
                            XElement(xname "summary", docComment).ToString().Split([|"\r\n"; "\r"; "\n"|], StringSplitOptions.None)
                            |> Array.fold (fun (sb:StringBuilder) line -> sb.AppendLine("    /// " + line)) (StringBuilder())
                        // add the resource
                        let accessorBody =
                            match (generateLegacy, generateLiteral) with
                            | (true, true) -> sprintf "    [<Literal>]\n    let %s = \"%s\"" identifier name
                            | (true, false) -> sprintf "    let %s = \"%s\"" identifier name // the [<Literal>] attribute can't be used for FSharp.Core
                            | (false, _) ->
                                let isStringResource = node.Attribute(xname "type") |> isNull
                                match (isStringResource, generateGetObject) with
                                | (true, _) -> sprintf "    let %s() = GetString(\"%s\")" identifier name
                                | (false, true) -> sprintf "    let %s() = GetObject(\"%s\")" identifier name
                                | (false, false) -> "" // the target runtime doesn't support non-string resources
                                // TODO: When calling the `GetObject` version, parse the `type` attribute to discover the proper return type
                        sb.AppendLine().Append(commentBody).AppendLine(accessorBody)
                    ) sb
                File.WriteAllText(sourcePath, body.ToString())
                printMessage <| sprintf "Done: %s" sourcePath
                Some(sourcePath)
        with e ->
            printf "An exception occurred when processing '%s'\n%s" resx (e.ToString())
            None

    [<Required>]
    member _.EmbeddedResource
        with get() = _embeddedText
         and set(value) = _embeddedText <- value

    [<Required>]
    member _.IntermediateOutputPath
        with get() = _outputPath
         and set(value) = _outputPath <- value

    member _.TargetFramework
        with get() = _targetFramework
         and set(value) = _targetFramework <- value

    [<Output>]
    member _.GeneratedSource
        with get() = _generatedSource

    interface ITask with
        member _.BuildEngine
            with get() = _buildEngine
             and set(value) = _buildEngine <- value

        member _.HostObject
            with get() = _hostObject
             and set(value) = _hostObject <- value

        member this.Execute() =
            let getBooleanMetadata (metadataName:string) (defaultValue:bool) (item:ITaskItem) =
                match item.GetMetadata(metadataName) with
                | value when String.IsNullOrWhiteSpace(value) -> defaultValue
                | value ->
                    match value.ToLowerInvariant() with
                    | "true" -> true
                    | "false" -> false
                    | _ -> failwith (sprintf "Expected boolean value for '%s' found '%s'" metadataName value)
            let mutable success = true
            let generatedSource =
                [| for item in this.EmbeddedResource do
                    if getBooleanMetadata "GenerateSource" false item then
                        let moduleName =
                            match item.GetMetadata("GeneratedModuleName") with
                            | null | "" -> Path.GetFileNameWithoutExtension(item.ItemSpec)
                            | value -> value
                        let generateLegacy = getBooleanMetadata "GenerateLegacyCode" false item
                        let generateLiteral = getBooleanMetadata "GenerateLiterals" true item
                        match generateSource item.ItemSpec moduleName generateLegacy generateLiteral with
                        | Some (source) -> yield TaskItem(source) :> ITaskItem
                        | None -> success <- false
                |]
            _generatedSource <- generatedSource
            success
