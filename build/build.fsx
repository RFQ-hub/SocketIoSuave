#r @"../packages/build/FAKE/tools/FakeLib.dll"
#load "./TaskDefinitionHelper.fsx"

open Fake
open Fake.AssemblyInfoFile
open Fake.ReleaseNotesHelper
open Fake.Testing.Expecto
open System
open System.IO
open BlackFox

#load "../packages/build/SourceLink.Fake/tools/Fake.fsx"

let configuration = "Release"
let rootDir = Path.GetFullPath(__SOURCE_DIRECTORY__ </> "..")
let artifactsDir = rootDir </> "artifacts"
let sourceDir = rootDir </> "src"
let mainBinDir = artifactsDir </> "bin" </> "SocketIoSuave" </> configuration

let company = "ITG"
let project = "SocketIoSuave"
let summary = "Socket.IO implementation for Suave web server"
let solutionFile  = rootDir </> project + ".sln"
let testAssemblies = artifactsDir </> "bin" </> "*.Tests" </> configuration </> "*.Tests.exe"
let packagedProjects = !! (rootDir </> "src/SocketIoSuave/*.??proj")

/// The profile where the project is posted
let gitOwner = "RFQ-hub"
let gitHome = "https://github.com/" + gitOwner

/// The name of the project on GitHub
let gitName = "SocketIoSuave"

/// The url for the raw files hosted
let gitRaw = environVarOrDefault "gitRaw" ("https://raw.github.com/" + gitOwner)

// --------------------------------------------------------------------------------------
// Build steps
// --------------------------------------------------------------------------------------

let inline versionPartOrZero x = if x < 0 then 0 else x

// Read additional information from the release notes document
let release =
    let fromFile = LoadReleaseNotes (rootDir </> "Release Notes.md")
    if buildServer = AppVeyor then
        let appVeyorBuildVersion = int appVeyorBuildVersion
        let nugetVer = sprintf "%s-appveyor%04i" fromFile.NugetVersion appVeyorBuildVersion
        let asmVer = System.Version.Parse(fromFile.AssemblyVersion)
        let asmVer =
            System.Version(
                versionPartOrZero asmVer.Major,
                versionPartOrZero asmVer.Minor,
                versionPartOrZero asmVer.Build,
                versionPartOrZero appVeyorBuildVersion)
        ReleaseNotes.New(asmVer.ToString(), nugetVer, fromFile.Date, fromFile.Notes)
    else
        fromFile

AppVeyor.UpdateBuild (fun info -> { info with Version = release.AssemblyVersion })

// Helper active pattern for project types
let (|Fsproj|Csproj|Vbproj|) (projFileName:string) =
    match projFileName with
    | f when f.EndsWith("fsproj") -> Fsproj
    | f when f.EndsWith("csproj") -> Csproj
    | f when f.EndsWith("vbproj") -> Vbproj
    | _                           -> failwith (sprintf "Project file %s not supported. Unknown project type." projFileName)

// Generate assembly info files with the right version & up-to-date information
task "AssemblyInfo" ["?Clean"] {
    let getAssemblyInfoAttributes projectName =
        [ Attribute.Title (projectName)
          Attribute.Product project
          Attribute.Description summary
          Attribute.Version release.AssemblyVersion
          Attribute.FileVersion release.AssemblyVersion
          Attribute.Company company ]

    let getProjectDetails projectPath =
        let projectName = System.IO.Path.GetFileNameWithoutExtension(projectPath)
        ( projectPath,
          projectName,
          System.IO.Path.GetDirectoryName(projectPath),
          (getAssemblyInfoAttributes projectName)
        )

    packagedProjects
    |> Seq.map getProjectDetails
    |> Seq.iter (fun (projFileName, projectName, folderName, attributes) ->
        match projFileName with
        | Fsproj -> CreateFSharpAssemblyInfo (folderName </> "AssemblyInfo.fs") attributes
        | Csproj -> CreateCSharpAssemblyInfo (folderName </> "Properties" </> "AssemblyInfo.cs") attributes
        | Vbproj -> CreateVisualBasicAssemblyInfo (folderName </> "My Project" </> "AssemblyInfo.vb") attributes
        )
}

// --------------------------------------------------------------------------------------
// Clean build results

task "Clean" [] {
    CleanDir artifactsDir

    !! solutionFile
    |> MSBuildRelease "" "Clean"
    |> ignore
}

// --------------------------------------------------------------------------------------
// Build library & test project

task "Build" ["AssemblyInfo"] {
    !! solutionFile
    |> MSBuildRelease "" "Rebuild"
    |> ignore
}

// --------------------------------------------------------------------------------------
// Run the unit tests using test runner

task "Test" [ "Build"] {
    !! testAssemblies
        |> Expecto (fun p ->
            { p with
                FailOnFocusedTests = true
                Parallel = false
            })
}

// --------------------------------------------------------------------------------------
// SourceLink allows Source Indexing on the PDB generated by the compiler, this allows
// the ability to step through the source code of external libraries http://ctaggart.github.io/SourceLink/

open SourceLink

task "SourceLink" [ "Build" ] {
    let baseUrl = sprintf "%s/%s/{0}/%%var2%%" gitRaw gitName
    tracefn "SourceLink base URL: %s" baseUrl

    packagedProjects
    |> Seq.iter (fun projFile ->
        let projectName = Path.GetFileNameWithoutExtension projFile
        let proj = VsProj.LoadRelease projFile
        tracefn "Generating SourceLink for %s on pdb: %s" projectName proj.OutputFilePdb
        SourceLink.Index proj.CompilesNotLinked proj.OutputFilePdb rootDir baseUrl
    )
}

let noSourceLink = hasBuildParam "NoSourceLink"
let finalBinaries = if isMono || noSourceLink then "Build" else "SourceLink"

EmptyTask "FinalBinaries" [ finalBinaries ]

// --------------------------------------------------------------------------------------
// Build a Zip package

let zipPath = artifactsDir </> (sprintf "%s-%s.zip" project release.NugetVersion)

task "Zip" ["FinalBinaries"] {
    let comment = sprintf "%s v%s" project release.NugetVersion
    let files =
        !! (mainBinDir </> "SocketIoSuave*.dll")
        ++ (mainBinDir </> "SocketIoSuave*.config")
    ZipHelper.CreateZip mainBinDir zipPath comment 9 false files

    AppVeyor.PushArtifact (fun p ->
        { p with
            Path = zipPath
            FileName = Path.GetFileName(zipPath)
            DeploymentName = "Binaries"
        })
}

// --------------------------------------------------------------------------------------
// Build a NuGet package

task "NuGet" ["FinalBinaries"] {
    Paket.Pack <| fun p ->
        { p with
            OutputPath = artifactsDir
            Version = release.NugetVersion
            ReleaseNotes = toLines release.Notes
            WorkingDir = sourceDir }

    !! (artifactsDir </> "*.nupkg")
    |> AppVeyor.PushArtifacts
}

task "PublishNuget" ["Clean", "NuGet"] {
    let key =
        match environVarOrNone "nuget-key" with
        | Some(key) -> key
        | None -> getUserPassword "NuGet key: "

    Paket.Push <| fun p ->  { p with WorkingDir = artifactsDir; ApiKey = key }
}

// --------------------------------------------------------------------------------------
// Release Scripts

#load "../paket-files/build/fsharp/FAKE/modules/Octokit/Octokit.fsx"

task "GitRelease" [] {
    let remote =
        Git.CommandHelper.getGitResult "" "remote -v"
        |> Seq.filter (fun (s: string) -> s.EndsWith("(push)"))
        |> Seq.tryFind (fun (s: string) -> s.Contains(gitOwner + "/" + gitName))
        |> function None -> gitHome + "/" + gitName | Some (s: string) -> s.Split().[0]

    Git.Staging.StageAll ""
    Git.Commit.Commit "" (sprintf "Bump version to %s" release.NugetVersion)
    Git.Branches.pushBranch "" remote (Git.Information.getBranchName "")

    Git.Branches.tag "" release.NugetVersion
    Git.Branches.pushTag "" remote release.NugetVersion
}

task "GitHubRelease" ["Zip"] {
    let user =
        match getBuildParam "github-user" with
        | s when not (String.IsNullOrWhiteSpace s) -> s
        | _ -> getUserInput "GitHub Username: "
    let pw =
        match getBuildParam "github-pw" with
        | s when not (String.IsNullOrWhiteSpace s) -> s
        | _ -> getUserPassword "GitHub Password or Token: "

    // release on github
    Octokit.createClient user pw
    |> Octokit.createDraft gitOwner gitName release.NugetVersion (release.SemVer.PreRelease <> None) release.Notes
    |> Octokit.uploadFile zipPath
    |> Octokit.releaseDraft
    |> Async.RunSynchronously
}

// --------------------------------------------------------------------------------------
// Empty targets for readability

EmptyTask "Default" ["Test"]
EmptyTask "Packages" ["Zip"; "NuGet"]
EmptyTask "Release" ["GitHubRelease"; "PublishNuget"]
EmptyTask "CI" ["Clean"; "Test"; "Packages"]

// --------------------------------------------------------------------------------------
// Go! Go! Go!

RunTaskOrDefault "Default"
