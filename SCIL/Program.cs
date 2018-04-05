﻿using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using CommandLine;
using SCIL.Analyzers;
using SCIL.Decompressor;
using SCIL.Logger;
using SCIL.Writer;

namespace SCIL
{
    class Program
    {
        static void Main(string[] args)
        {
            CommandLine.Parser.Default.ParseArguments<ConsoleOptions>(args)
                .WithParsed<ConsoleOptions>(RunOptionsAndReturnExitCode)
                .WithNotParsed(error => {});
#if DEBUG
            Console.ReadKey();
#endif
        }

        private static void RunOptionsAndReturnExitCode(ConsoleOptions opts)
        {
            Run(opts).GetAwaiter().GetResult();
        }

        private static async Task Run(ConsoleOptions opts)
        {
            // Check output path
            var outputPathInfo = new DirectoryInfo(opts.OutputPath);
            if (!outputPathInfo.Exists)
            {
                outputPathInfo.Create();
            }

            // Load instruction emitters
            var emitterInterface = typeof(IInstructionEmitter);
            var emitters = Assembly.GetExecutingAssembly().DefinedTypes
                .Where(e => e.ImplementedInterfaces.Any(i => i == emitterInterface) &&
                            e.CustomAttributes.All(attr => typeof(IgnoreEmitterAttribute) != attr.AttributeType))
                .OrderBy(e =>
                    e.CustomAttributes.Any(attr => typeof(EmitterOrderAttribute) == attr.AttributeType)
                        ? e.GetCustomAttribute<EmitterOrderAttribute>().Order
                        : 10)
                .Select(Activator.CreateInstance)
                .Cast<IInstructionEmitter>()
                .ToList();

            // Count instructions
            var instructionCounter = new TotalInstructionCounter();
            if (opts.CountInstructions)
            {
                emitters.Insert(0, instructionCounter);
            }

            // Create logger
            var logger = new ConsoleLogger(opts.Verbose, opts.Wait);

            // Create module writer
            IModuleWriter moduleWriter = opts.NoOutput ? 
                (IModuleWriter) new NoOutputWriter() 
                : opts.SingleFile ? 
                (IModuleWriter) new SingleFileWriter(outputPathInfo.FullName) 
                : new ModuleWriter(outputPathInfo.FullName);
            
            // Check for input file
            if (!string.IsNullOrWhiteSpace(opts.InputFile))
            {
                // Check if path is input file
                var fileInfo = new FileInfo(opts.InputFile);
                if (fileInfo.Exists)
                {
                    await AnalyzeFile(fileInfo, emitters, logger, moduleWriter, opts);
                }
                else
                {
                    logger.Log($"File {opts.InputFile} not found");
                }
                return;
            }

            // Check for input path
            if (!string.IsNullOrWhiteSpace(opts.InputPath))
            {
                var pathInfo = new DirectoryInfo(opts.InputPath);
                if (pathInfo.Exists)
                {
                    var searchOption = opts.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

                    foreach (var file in 
                        Directory.GetFiles(pathInfo.FullName, "*.apk", searchOption)
                            .Concat(Directory.GetFiles(pathInfo.FullName, "*.exe", searchOption))
                            .Concat(Directory.GetFiles(pathInfo.FullName, "*.dll", searchOption)))
                    {
                        var fileInfo = new FileInfo(file);
                        await AnalyzeFile(fileInfo, emitters, logger, moduleWriter, opts);
                    }
                }
                else
                {
                    logger.Log($"Path {opts.InputPath} not found");
                }

                return;
            }

            logger.Log("Please select file or path");
        }

        private static async Task AnalyzeFile(FileInfo fileInfo, IReadOnlyCollection<IInstructionEmitter> emitters, ILogger logger, IModuleWriter moduleWriter, ConsoleOptions opts)
        {
            // Logging
            logger.Log("Analyzing file " + fileInfo.FullName);

            // Detect if file is zip
            if (await ZipHelper.CheckSignature(fileInfo.FullName))
            {
                await LoadZip(fileInfo, emitters, logger, moduleWriter, opts);
            }
            else
            {
                // TODO : Detect dll and exe
                // Just jump out into the water and see if we survive (no exceptions)
                await ProcessAssembly(fileInfo.OpenRead(), moduleWriter, emitters, logger);
            }
        }

        private static async Task LoadZip(FileInfo fileInfo, IReadOnlyCollection<IInstructionEmitter> emitters, ILogger logger, IModuleWriter moduleWriter, ConsoleOptions opts)
        {
            // Open zip file
            using (var zipFile = ZipFile.OpenRead(fileInfo.FullName))
            {
                var assemblies = zipFile.Entries.Where(entry => FilterXamarinAssembliesDlls(entry) || FilterUnityAssembliesDlls(entry)).ToList();
                foreach (var assembly in assemblies)
                {
                    if (opts.Excluded.Contains(assembly.Name))
                    {
                        logger.Log("Skipping excluded assembly: " + assembly.Name);
                    }
                    else
                    {
                        logger.Log("Loading zip entry: " + assembly.FullName);

                        using (var stream = new MemoryStream())
                        {
                            await assembly.Open().CopyToAsync(stream);

                            // Set position 0
                            stream.Position = 0;

                            await ProcessAssembly(stream, moduleWriter, emitters, logger);
                        }
                    }
                }

                // If no assemblies was found - try to find bundle
                if (!assemblies.Any())
                {
                    logger.Log("No assemblies found in zip");

                    var libmonodroidbundle = zipFile.Entries.Where(entry =>
                            entry.FullName.StartsWith("lib", StringComparison.OrdinalIgnoreCase) &&
                            entry.FullName.EndsWith("libmonodroid_bundle_app.so", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (libmonodroidbundle.Any())
                    {
                        ZipArchiveEntry selectedLibMonoDroidBundle;
                        if (libmonodroidbundle.Count() == 1)
                        {
                            selectedLibMonoDroidBundle = libmonodroidbundle.First();
                        }
                        else
                        {
                            selectedLibMonoDroidBundle =
                                libmonodroidbundle.FirstOrDefault(e => e.FullName.Contains("armeabi-v7a")) ?? libmonodroidbundle.First();
                        }

                        // Read bundle
                        logger.Log("Loading " + selectedLibMonoDroidBundle.FullName);
                        using (var stream = new MemoryStream())
                        {
                            await selectedLibMonoDroidBundle.Open().CopyToAsync(stream);

                            // Set position 0
                            stream.Position = 0;

                            var files = await XamarinBundleUnpack.GetGzippedAssemblies(stream.ToArray());
                            foreach (var file in files)
                            {
                                using (var memStream = new MemoryStream(file))
                                {
                                    await ProcessAssembly(memStream, moduleWriter, emitters, logger);
                                }
                            }
                        }
                    }
                    else
                    {
                        logger.Log("libmonodroid_bundle_app.so not found");
                    }
                }
            }
        }

        private static Task ProcessAssembly(Stream stream, IModuleWriter moduleWriter, IReadOnlyCollection<IInstructionEmitter> emitters, ILogger logger)
        {
            var moduleProcessor = new ModuleProcessor(emitters, moduleWriter, logger);
            return moduleProcessor.ProcessAssembly(stream);
        }

        private static bool FilterXamarinAssembliesDlls(ZipArchiveEntry entry)
        {
            return entry.FullName.StartsWith("assemblies", StringComparison.OrdinalIgnoreCase) &&
                   entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        }

        private static bool FilterUnityAssembliesDlls(ZipArchiveEntry entry)
        {
            return entry.FullName.StartsWith("assets/bin/Data/Managed/", StringComparison.OrdinalIgnoreCase) &&
                   entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        }
    }

    class ConsoleOptions
    {
        [Option('f', "InputFile", Required = false, HelpText = "Apk files to be processed.")]
        public string InputFile { get; set; }

        [Option('p', "InputPath", Required = false, HelpText = "Input path to search for apk or dll/exe files")]
        public string InputPath { get; set; }

        [Option('o', "OutputPath", Required = true, HelpText = "Output path")]
        public string OutputPath { get; set; }

        [Option('e', "excluded", Required = false, HelpText = "Assemblies to exclude")]
        public IEnumerable<string> Excluded { get; set; }

        [Option("noOutput", Required = false, HelpText = "No output")]
        public bool NoOutput { get; set; }

        [Option("verbose", Required = false, HelpText = "Verbose output")]
        public bool Verbose { get; set; }

        [Option('s', "singlefile", Required = false, HelpText = "Write output to a single file")]
        public bool SingleFile { get; set; }

        [Option('w', "wait",  Required = false, HelpText = "Wait for some error messages")]
        public bool Wait { get; set; }

        [Option('r', "recursive", Required = false, HelpText = "Recursive search input path")]
        public bool Recursive { get; set; }

        [Option('c', "countInstructions", Required = false, HelpText = "Count number of instructions for each module")]
        public bool CountInstructions { get; set; }
    }
}
