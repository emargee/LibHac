﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using static LibHacBuild.CodeGen.Common;

namespace LibHacBuild.CodeGen.Stage1
{
    public static class ResultCodeGen
    {
        // RyuJIT will always be inlined a function if its CIL size is <= 0x10 bytes
        private const int InlineThreshold = 0x10;

        public static void Run()
        {
            ModuleInfo[] modules = ReadResults();

            SetEmptyResultValues(modules);
            ValidateResults(modules);
            CheckForDuplicates(modules);
            ValidateHierarchy(modules);
            CheckIfAggressiveInliningNeeded(modules);

            foreach (ModuleInfo module in modules.Where(x => !string.IsNullOrWhiteSpace(x.Path)))
            {
                string moduleResultFile = PrintModule(module);

                WriteOutput(module.Path, moduleResultFile);
            }

            byte[] archive = BuildArchive(modules);
            byte[] compressedArchive = Build.DeflateBytes(archive);
            string archiveStr = PrintArchive(compressedArchive);
            WriteOutput("LibHac/ResultNameResolver.Generated.cs", archiveStr);

            string enumStr = PrintEnum(modules);
            WriteOutput("../.tmp/result_enums.txt", enumStr);
        }

        private static ModuleInfo[] ReadResults()
        {
            ModuleIndex[] moduleNames = ReadCsv<ModuleIndex>("result_modules.csv");
            ModulePath[] modulePaths = ReadCsv<ModulePath>("result_paths.csv");
            ResultInfo[] results = ReadCsv<ResultInfo>("results.csv");

            var modules = new Dictionary<string, ModuleInfo>();

            foreach (ModuleIndex name in moduleNames)
            {
                var module = new ModuleInfo();
                module.Name = name.Name;
                module.Index = name.Index;

                modules.Add(name.Name, module);
            }

            foreach (ModulePath path in modulePaths)
            {
                ModuleInfo module = modules[path.Name];
                module.Namespace = path.Namespace;
                module.Path = path.Path;
            }

            foreach (ModuleInfo module in modules.Values)
            {
                module.Results = results.Where(x => x.Module == module.Index).OrderBy(x => x.DescriptionStart)
                    .ToArray();
            }

            return modules.Values.ToArray();
        }

        private static void SetEmptyResultValues(ModuleInfo[] modules)
        {
            foreach (ModuleInfo module in modules)
            {
                foreach (ResultInfo result in module.Results)
                {
                    result.FullName = $"Result{module.Name}{result.Name}";

                    if (string.IsNullOrWhiteSpace(result.Name))
                    {
                        if (result.IsRange)
                        {
                            result.Name += $"Range{result.DescriptionStart}To{result.DescriptionEnd}";
                        }
                        else
                        {
                            result.Name = $"Result{result.DescriptionStart}";
                            result.DescriptionEnd = result.DescriptionStart;
                        }
                    }
                }
            }
        }

        private static void ValidateResults(ModuleInfo[] modules)
        {
            foreach (ModuleInfo module in modules)
            {
                foreach (ResultInfo result in module.Results)
                {
                    // Logic should match Result.Base.ctor
                    Assert(1 <= result.Module && result.Module < 512, "Invalid Module");
                    Assert(0 <= result.DescriptionStart && result.DescriptionStart < 8192, "Invalid Description Start");
                    Assert(0 <= result.DescriptionEnd && result.DescriptionEnd < 8192, "Invalid Description End");
                    Assert(result.DescriptionStart <= result.DescriptionEnd, "descriptionStart must be <= descriptionEnd");

                    // ReSharper disable once ParameterOnlyUsedForPreconditionCheck.Local
                    void Assert(bool condition, string message)
                    {
                        if (!condition)
                            throw new InvalidDataException($"Result {result.Module}-{result.DescriptionStart}: {message}");
                    }
                }
            }
        }

        private static void CheckForDuplicates(ModuleInfo[] modules)
        {
            var moduleIndexSet = new HashSet<int>();
            var moduleNameSet = new HashSet<string>();

            foreach (ModuleInfo module in modules)
            {
                var descriptionSet = new HashSet<int>();

                if (!moduleIndexSet.Add(module.Index))
                {
                    throw new InvalidDataException($"Duplicate result module index {module.Index}.");
                }

                if (!moduleNameSet.Add(module.Name))
                {
                    throw new InvalidDataException($"Duplicate result module name {module.Name}.");
                }

                foreach (ResultInfo result in module.Results)
                {
                    if (!descriptionSet.Add(result.DescriptionStart))
                    {
                        throw new InvalidDataException($"Duplicate result {result.Module}-{result.DescriptionStart}-{result.DescriptionEnd}.");
                    }
                }
            }
        }

        private static void ValidateHierarchy(ModuleInfo[] modules)
        {
            foreach (ModuleInfo module in modules)
            {
                var hierarchy = new Stack<ResultInfo>();

                foreach (ResultInfo result in module.Results)
                {
                    while (hierarchy.Count > 0 && hierarchy.Peek().DescriptionEnd < result.DescriptionStart)
                    {
                        hierarchy.Pop();
                    }

                    if (result.IsRange)
                    {
                        if (hierarchy.Count > 0 && result.DescriptionEnd > hierarchy.Peek().DescriptionEnd)
                        {
                            throw new InvalidDataException($"Result {result.Module}-{result.DescriptionStart} is not nested properly.");
                        }

                        hierarchy.Push(result);
                    }
                }
            }
        }

        private static void CheckIfAggressiveInliningNeeded(ModuleInfo[] modules)
        {
            foreach (ModuleInfo module in modules)
            {
                module.NeedsAggressiveInlining = module.Results.Any(x => EstimateCilSize(x) > InlineThreshold);
            }
        }

        private static string PrintModule(ModuleInfo module)
        {
            var sb = new IndentingStringBuilder();

            sb.AppendLine(GetHeader());
            sb.AppendLine();

            if (module.NeedsAggressiveInlining)
            {
                sb.AppendLine("using System.Runtime.CompilerServices;");
                sb.AppendLine();
            }

            sb.AppendLine($"namespace {module.Namespace}");
            sb.AppendLineAndIncrease("{");

            sb.AppendLine($"public static class Result{module.Name}");
            sb.AppendLineAndIncrease("{");

            sb.AppendLine($"public const int Module{module.Name} = {module.Index};");
            sb.AppendLine();

            var hierarchy = new Stack<ResultInfo>();
            bool justIndented = false;

            foreach (ResultInfo result in module.Results)
            {
                while (hierarchy.Count > 0 && hierarchy.Peek().DescriptionEnd < result.DescriptionStart)
                {
                    hierarchy.Pop();
                    sb.DecreaseLevel();
                    sb.AppendSpacerLine();
                }

                if (!justIndented && result.IsRange)
                {
                    sb.AppendSpacerLine();
                }

                PrintResult(sb, module.Name, result);

                if (result.IsRange)
                {
                    hierarchy.Push(result);
                    sb.IncreaseLevel();
                }

                justIndented = result.IsRange;
            }

            while (hierarchy.Count > 0)
            {
                hierarchy.Pop();
                sb.DecreaseLevel();
            }

            sb.DecreaseAndAppendLine("}");
            sb.DecreaseAndAppendLine("}");

            return sb.ToString();
        }

        private static void PrintResult(IndentingStringBuilder sb, string moduleName, ResultInfo result)
        {
            string descriptionArgs;

            if (result.IsRange)
            {
                descriptionArgs = $"{result.DescriptionStart}, {result.DescriptionEnd}";
            }
            else
            {
                descriptionArgs = $"{result.DescriptionStart}";
            }

            sb.AppendLine(GetXmlDoc(result));

            string resultCtor = $"new Result.Base(Module{moduleName}, {descriptionArgs});";
            sb.Append($"public static Result.Base {result.Name} ");

            if (EstimateCilSize(result) > InlineThreshold)
            {
                sb.AppendLine($"{{ [MethodImpl(MethodImplOptions.AggressiveInlining)] get => {resultCtor} }}");
            }
            else
            {
                sb.AppendLine($"=> {resultCtor}");
            }
        }

        private static string GetXmlDoc(ResultInfo result)
        {
            string doc = "/// <summary>";

            if (!string.IsNullOrWhiteSpace(result.Summary))
            {
                doc += $"{result.Summary}<br/>";
            }

            doc += $"Error code: {result.ErrorCode}";

            if (result.IsRange)
            {
                doc += $"; Range: {result.DescriptionStart}-{result.DescriptionEnd}";
            }

            doc += $"; Inner value: 0x{result.InnerValue:x}";
            doc += "</summary>";

            return doc;
        }

        private static byte[] BuildArchive(ModuleInfo[] modules)
        {
            var builder = new ResultArchiveBuilder();

            foreach (ModuleInfo module in modules.OrderBy(x => x.Index))
            {
                foreach (ResultInfo result in module.Results.OrderBy(x => x.DescriptionStart))
                {
                    builder.Add(result);
                }
            }

            return builder.Build();
        }

        private static string PrintArchive(ReadOnlySpan<byte> data)
        {
            var sb = new IndentingStringBuilder();

            sb.AppendLine(GetHeader());
            sb.AppendLine();

            sb.AppendLine("using System;");
            sb.AppendLine();

            sb.AppendLine("namespace LibHac");
            sb.AppendLineAndIncrease("{");

            sb.AppendLine("internal partial class ResultNameResolver");
            sb.AppendLineAndIncrease("{");

            sb.AppendLine("private static ReadOnlySpan<byte> ArchiveData => new byte[]");
            sb.AppendLineAndIncrease("{");

            for (int i = 0; i < data.Length; i++)
            {
                if (i % 16 != 0) sb.Append(" ");
                sb.Append($"0x{data[i]:x2}");

                if (i != data.Length - 1)
                {
                    sb.Append(",");
                    if (i % 16 == 15) sb.AppendLine();
                }
            }

            sb.AppendLine();
            sb.DecreaseAndAppendLine("};");
            sb.DecreaseAndAppendLine("}");
            sb.DecreaseAndAppendLine("}");

            return sb.ToString();
        }

        private static T[] ReadCsv<T>(string name)
        {
            using (var csv = new CsvReader(new StreamReader(GetResource(name)), CultureInfo.InvariantCulture))
            {
                csv.Configuration.AllowComments = true;
                csv.Configuration.DetectColumnCountChanges = true;

                if (typeof(T) == typeof(ResultInfo))
                {
                    csv.Configuration.RegisterClassMap<ResultMap>();
                }

                return csv.GetRecords<T>().ToArray();
            }
        }

        private static int EstimateCilSize(ResultInfo result)
        {
            int size = 0;

            size += GetLoadSize(result.Module);
            size += GetLoadSize(result.DescriptionStart);

            if (result.IsRange)
                size += GetLoadSize(result.DescriptionEnd);

            size += 5; // newobj
            size += 1; // ret

            return size;

            static int GetLoadSize(int value)
            {
                if (value >= -1 && value <= 8)
                    return 1; // ldc.i4.X

                if (value >= sbyte.MinValue && value <= sbyte.MaxValue)
                    return 2; // ldc.i4.s XX

                return 5; // ldc.i4 XXXXXXXX
            }
        }

        public static string PrintEnum(ModuleInfo[] modules)
        {
            var sb = new StringBuilder();
            int[] printUnknownResultsForModules = { 2 };
            int[] skipModules = { 428 };

            foreach (ModuleInfo module in modules.Where(x => !skipModules.Contains(x.Index)))
            {
                bool printAllResults = printUnknownResultsForModules.Contains(module.Index);
                int prevResult = 1;

                foreach (ResultInfo result in module.Results)
                {
                    if (printAllResults && result.DescriptionStart > prevResult + 1)
                    {
                        for (int i = prevResult + 1; i < result.DescriptionStart; i++)
                        {
                            int innerValue = 2 & 0x1ff | ((i & 0x7ffff) << 9);
                            string unknownResultLine = $"Result_{result.Module}_{i} = {innerValue},";
                            sb.AppendLine(unknownResultLine);
                        }
                    }

                    string name = string.IsNullOrWhiteSpace(result.Name) ? string.Empty : $"_{result.Name}";
                    string line = $"Result_{result.Module}_{result.DescriptionStart}{name} = {result.InnerValue},";

                    sb.AppendLine(line);
                    prevResult = result.DescriptionStart;
                }

                if (printAllResults)
                {
                    for (int i = prevResult + 1; i < 8192; i++)
                    {
                        int innerValue = 2 & 0x1ff | ((i & 0x7ffff) << 9);
                        string unknownResultLine = $"Result_{module.Index}_{i} = {innerValue},";
                        sb.AppendLine(unknownResultLine);
                    }
                }
            }

            return sb.ToString();
        }
    }

    public class ResultArchiveBuilder
    {
        private List<ResultInfo> Results = new List<ResultInfo>();

        public void Add(ResultInfo result)
        {
            Results.Add(result);
        }

        public byte[] Build()
        {
            int tableOffset = CalculateNameTableOffset();
            var archive = new byte[tableOffset + CalculateNameTableSize()];

            ref HeaderStruct header = ref Unsafe.As<byte, HeaderStruct>(ref archive[0]);
            Span<Element> elements = MemoryMarshal.Cast<byte, Element>(
                archive.AsSpan(Unsafe.SizeOf<HeaderStruct>(), Results.Count * Unsafe.SizeOf<Element>()));
            Span<byte> nameTable = archive.AsSpan(tableOffset);

            header.ElementCount = Results.Count;
            header.NameTableOffset = tableOffset;

            int curNameOffset = 0;

            for (int i = 0; i < Results.Count; i++)
            {
                ResultInfo result = Results[i];
                ref Element element = ref elements[i];

                element.NameOffset = curNameOffset;
                element.Module = (short)result.Module;
                element.DescriptionStart = (short)result.DescriptionStart;
                element.DescriptionEnd = (short)result.DescriptionEnd;

                Span<byte> utf8Name = Encoding.UTF8.GetBytes(result.FullName);
                utf8Name.CopyTo(nameTable.Slice(curNameOffset));
                nameTable[curNameOffset + utf8Name.Length] = 0;

                curNameOffset += utf8Name.Length + 1;
            }

            return archive;
        }

        private int CalculateNameTableOffset()
        {
            return Unsafe.SizeOf<HeaderStruct>() + Unsafe.SizeOf<Element>() * Results.Count;
        }

        private int CalculateNameTableSize()
        {
            int size = 0;
            Encoding encoding = Encoding.UTF8;

            foreach (ResultInfo result in Results)
            {
                size += encoding.GetByteCount(result.FullName) + 1;
            }

            return size;
        }

        // ReSharper disable NotAccessedField.Local
        private struct HeaderStruct
        {
            public int ElementCount;
            public int NameTableOffset;
        }

        private struct Element
        {
            public int NameOffset;
            public short Module;
            public short DescriptionStart;
            public short DescriptionEnd;
        }
        // ReSharper restore NotAccessedField.Local
    }

    public class ModuleIndex
    {
        public string Name { get; set; }
        public int Index { get; set; }
    }

    public class ModulePath
    {
        public string Name { get; set; }
        public string Namespace { get; set; }
        public string Path { get; set; }
    }

    [DebuggerDisplay("{" + nameof(Name) + ",nq}")]
    public class ModuleInfo
    {
        public string Name { get; set; }
        public int Index { get; set; }
        public string Namespace { get; set; }
        public string Path { get; set; }

        public bool NeedsAggressiveInlining { get; set; }
        public ResultInfo[] Results { get; set; }
    }

    [DebuggerDisplay("{" + nameof(Name) + ",nq}")]
    public class ResultInfo
    {
        public int Module { get; set; }
        public int DescriptionStart { get; set; }
        public int DescriptionEnd { get; set; }
        public string Name { get; set; }
        public string FullName { get; set; }
        public string Summary { get; set; }

        public bool IsRange => DescriptionStart != DescriptionEnd;
        public string ErrorCode => $"{2000 + Module:d4}-{DescriptionStart:d4}";
        public int InnerValue => Module & 0x1ff | ((DescriptionStart & 0x7ffff) << 9);
    }

    public sealed class ResultMap : ClassMap<ResultInfo>
    {
        public ResultMap()
        {
            Map(m => m.Module);
            Map(m => m.Name);
            Map(m => m.Summary);
            Map(m => m.DescriptionStart);
            Map(m => m.DescriptionEnd).ConvertUsing(row =>
            {
                string field = row.GetField("DescriptionEnd");
                if (string.IsNullOrWhiteSpace(field))
                    field = row.GetField("DescriptionStart");

                return int.Parse(field);
            });
        }
    }
}
