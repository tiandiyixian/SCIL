﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using SCIL.Logger;

namespace SCIL.Flix
{
    public class FlixExecutor : IFlixExecutor
    {
        private readonly ILogger _logger;
        private readonly Configuration _configuration;
        private readonly string _tempPath;
        private readonly List<string> _compileFlixList = new List<string>();
        private string _flixPath;

        public FlixExecutor(ILogger logger, Configuration configuration)
        {
            _logger = logger;
            _configuration = configuration;
            _tempPath = Path.Combine(Path.GetTempPath(), "SCIL", Guid.NewGuid().ToString());
            Directory.CreateDirectory(_tempPath);

            // Extract
            ExtractFlix();
        }

        private void ExtractFlix()
        {
            var assembly = Assembly.GetExecutingAssembly();
            var resources = assembly.GetManifestResourceNames();
            foreach (var resource in resources)
            {
                // Skip non flix files and non Flix jar files
                if (!resource.EndsWith(".flix") && !resource.EndsWith("flix.jar"))
                {
                    continue;
                }

                // Extract file
                var outputPath = Path.Combine(_tempPath, resource);
                using (var fileStream = File.OpenWrite(outputPath))
                {
                    using (var resourceStrem = assembly.GetManifestResourceStream(resource))
                    {
                        resourceStrem.CopyTo(fileStream);
                    }
                }

                // Set flix jar file
                if (resource.EndsWith("flix.jar"))
                {
                    _flixPath = outputPath;
                }
                else
                {
                    // Set file list which should be compiled
                    _compileFlixList.Add(outputPath);
                }
            }
        }

        private static readonly ConcurrentDictionary<SemaphoreSlim, Process> _currentProcesses = new ConcurrentDictionary<SemaphoreSlim, Process>();

        static FlixExecutor()
        {
            Console.CancelKeyPress += (sender, eventArgs) =>
            {
                var copy = _currentProcesses.ToList();
                foreach (var sem in copy)
                {
                    if (_currentProcesses.TryRemove(sem.Key, out Process p))
                    {
                        p.Kill();
                    }
                }
            };
        }

        private async Task ExecuteFlix(string[] javaArgs, string[] flixArgs)
        {
            var fileName = "java";
            var arguments = GetArguments(javaArgs, flixArgs);

            ProcessStartInfo processInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                CreateNoWindow = !_configuration.ShowFlixWindow,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            try
            {
                Process process = Process.Start(processInfo);

                // Attach handlers for process
                process.OutputDataReceived += (sender, eventArgs) => _logger.Log(eventArgs.Data);
                process.ErrorDataReceived += (sender, eventArgs) => _logger.Log(eventArgs.Data);

                // Asynchronously read the standard output of the spawned process. 
                // This raises OutputDataReceived events for each line of output.
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                SemaphoreSlim semaphore = new SemaphoreSlim(0, 1);
                {
                    // Attach on process exited
                    process.Exited += (sender, eventArgs) => semaphore.Release();
                    process.EnableRaisingEvents = true;
                    if (process.HasExited)
                    {
                        semaphore.Release();
                    }

                    if (!_currentProcesses.TryAdd(semaphore, process))
                        throw new Exception();

                    // Wait for either Console.Cancel or Exit
                    await semaphore.WaitAsync().ConfigureAwait(false);
                }
            }
            catch(Exception ex)
            {
                throw;
            }
        }

        private string GetArguments(string[] javaArgs, string[] flixArgs)
        {
            var listArguments = new List<string>();

            // Add Java args
            listArguments.AddRange(javaArgs);
            listArguments.Add($"-jar {QuotePath(_flixPath)}");

            // Add Flix args
            listArguments.AddRange(_compileFlixList.Select(QuotePath));
            listArguments.AddRange(flixArgs);

            return string.Join(" ", listArguments);
        }

        private string QuotePath(string path) => $"\"{path}\"";

        public void Dispose()
        {
            try
            {
                Directory.Delete(_tempPath, true);
            }
            catch
            {
                // ignored since not fatal if we cannot clear our temp path
            }
        }

        public async Task Execute(IEnumerable<string> files)
        {
            // Java args
            IEnumerable<string> javaArgs;
            if (_configuration.JavaArgs.Any())
            {
                javaArgs = _configuration.JavaArgs;
            }
            else
            {
                // Use max heap of 16 gb
                javaArgs = new[] {"-Xmx16g"};
            }

            // Flix args
            var flixArgs = files.Select(QuotePath).ToList();

            if (_configuration.NoStringAnalysis)
                flixArgs.Remove("Definitions/StringAnalysis.flix");

            if (_configuration.FlixArgs.Any())
            {
                flixArgs = flixArgs.Concat(_configuration.FlixArgs).ToList();
            }
            else
            {
                flixArgs = flixArgs.Concat(new List<string>()
                {
                    "--print Sources,Sinks,TaintListStack,TaintListLocalVar,TaintListArg,TaintListTask,PointerTable,StringLattice,SecretStrings,Results"
                }).ToList();
            }

            // Execute flix
            await ExecuteFlix(javaArgs.ToArray(), flixArgs.ToArray());
        }
    }
}
