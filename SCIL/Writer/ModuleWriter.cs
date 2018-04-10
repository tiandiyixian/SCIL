﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Mono.Cecil;

namespace SCIL.Writer
{
    class ModuleWriter : IModuleWriter
    {
        public DirectoryInfo Directory { get; }

        public ModuleWriter(string path)
        {
            Directory = new DirectoryInfo(path);

            if (!Directory.Exists)
                Directory.Create();
        }

        public IEnumerable<string> GetCreatedFilesAndReset()
        {
            throw new NotSupportedException();
        }

        public Task<IModuleWriter> GetAssemblyModuleWriter(string name)
        {
            return Task.FromResult((IModuleWriter) new ModuleWriter(Directory.CreateSubdirectory(name).FullName));
        }

        public Task<IModuleWriter> GetTypeModuleWriter(TypeDefinition typeDefinition)
        {
            var typePath = GetSubpathName(typeDefinition, Directory);
            return Task.FromResult((IModuleWriter) new ModuleWriter(typePath.FullName));
        }

        public Task WriteMethod(TypeDefinition typeDefinition, MethodDefinition methodDefinition) =>
            WriteMethod(typeDefinition, methodDefinition, "");

        public async Task WriteMethod(TypeDefinition typeDefinition, MethodDefinition methodDefinition, string methodBody)
        {
            var directory = GetSubpathName(typeDefinition, Directory);
            var fileInfo = new FileInfo(Path.Combine(directory.FullName, "method_" + GetSafePath(methodDefinition.Name) + ".flix"));

            await File.WriteAllTextAsync(fileInfo.FullName, methodBody.TrimEnd()).ConfigureAwait(false);
        }

        private static DirectoryInfo GetSubpathName(TypeDefinition type, DirectoryInfo outputDirectory)
        {
            var fullName = GetSafePath(type.FullName);

            return outputDirectory.CreateSubdirectory(fullName);
        }

        private static string GetSafePath(string input)
        {
            return new string(input.Where(Char.IsLetterOrDigit).ToArray());
        }

        public void Dispose()
        {
        }
    }
}