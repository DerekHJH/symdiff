﻿using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Differencing;
using Microsoft.VisualStudio.Utilities;
using SourceAnalyzer;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Microsoft.TeamFoundation.VersionControl.Client;
using Microsoft.TeamFoundation.VersionControl.Common;
using Microsoft.Boogie; 



namespace SyntaxDiff
{
    /// <summary>
    /// Generate the syntax diff information
    /// </summary>
    class SyntaxDiffManager
    {
        static  bool useVisualStudioMEFDiff = false;
        static SDiff.Config v1v2Config = null;
        static void Main(string[] args)
        {
            if (args.Count() < 3)
            {
                Console.WriteLine("Usage: v1.bpl v2.bpl v1v2.config");
                Console.WriteLine("     The source files are implicitly specified inside the bpl files");
                Console.WriteLine("     v1v2.config: a mapping between entities in v1.bpl and v2.bpl (default generated by SymDiff.exe -inferConfig v1.bpl v2.bpl)");
                return;
            }

            if (args.Any(x => x == "-break"))
                Debugger.Launch();

            var v1 = args[0];
            var v2 = args[1];
            v1v2Config= new SDiff.Config(args[2]);
            Debug.Assert(v1v2Config != null);
            var v1Prog = SDiff.Boogie.Process.ParseProgram(v1);
            var v2Prog = SDiff.Boogie.Process.ParseProgram(v2);

            var v1srcInfo = new SourceInfoManager(v1Prog, Path.GetDirectoryName(v1));
            v1srcInfo.ComputeSourceInfoForImplementations();
            var v2srcInfo = new SourceInfoManager(v2Prog, Path.GetDirectoryName(v2));
            v2srcInfo.ComputeSourceInfoForImplementations();

            //remove any sourcefile/sourceline info before doing a syntactic diff
            new RemoveSrcInfoStmts().Visit(v1Prog);
            new RemoveSrcInfoStmts().Visit(v2Prog);

            //remove those implementations that are syntactically identical
            var diffImpls = FindImplementsWithDifferentBodies(v1Prog, v2Prog);

            //perform the diff on the source files in which the implemnation pair is present
            foreach(var i12 in diffImpls)
            {
                var d12 = FindDiffSourceLinesInImplementation(i12.Item1, v1srcInfo, i12.Item2, v2srcInfo);

                PrintChangedLinesForProcedure("v0", i12.Item1.Name, d12.Item1);
                PrintChangedLinesForProcedure("v1", i12.Item2.Name, d12.Item2);                
            }

            return; 

            if (!useVisualStudioMEFDiff)
                new DiffUsingTFS(args);
            else
                new DiffAnalyzerUsingMEFAndVisualStudio().PerformDiffString(args[0], args[1]);
        }

        private static void PrintChangedLinesForProcedure(string fn, string procName, List<int> lines)
        {
            if (lines.Count > 0)
            {
                Console.WriteLine("Diff for {0}:", fn);
                foreach (var lineNo in lines)
                {
                    Console.WriteLine("{0}, {1}", procName, lineNo);
                }
            }
        }

        private static Tuple<List<int>, List<int>> FindDiffSourceLinesInImplementation(Implementation implementation1, SourceInfoManager v1srcInfo, Implementation implementation2, SourceInfoManager v2srcInfo)
        {
            if (implementation1 != null && implementation2 != null)
            {
                Console.WriteLine("----Diffing {0} and {1}-----", implementation1.Name, implementation2.Name);
                var v1Lines = v1srcInfo.GetSrcLinesForImpl(implementation1);
                var v2Lines = v2srcInfo.GetSrcLinesForImpl(implementation2);
                var diffLines = new DiffUsingTFS().DiffStrings(v1Lines, v2Lines);
                //add the offset to any difflines
                var i1StartLine = v1srcInfo.GetStartLineForImplInFile(implementation1);
                var i2StartLine = v2srcInfo.GetStartLineForImplInFile(implementation2);
                var d1 = diffLines.Item1.Select(x => x + i1StartLine).ToList();
                var d2 = diffLines.Item2.Select(x => x + i2StartLine).ToList();
                return Tuple.Create(d1, d2);
            }
            //TODO: if implementation_i is null, then add all the lines in implementation2 
            throw new NotImplementedException();
        }
        /// <summary>
        /// Returns implementations in the two versions with a difference in string representation of body
        /// Uses the Config file to search for mapping
        /// Returns a list of (i1, i2) that have different implementations, with one of i1/i2 being null if absent
        /// </summary>
        /// <param name="v1Prog"></param>
        /// <param name="v2Prog"></param>
        /// <returns></returns>
        private static IEnumerable<Tuple<Implementation,Implementation>> FindImplementsWithDifferentBodies(Program v1Prog, Program v2Prog)
        {
            var v1Impls = new Dictionary<string, Implementation>();
            var v2Impls = new Dictionary<string, Implementation>();
            v1Prog.Implementations.Iter(i => v1Impls[i.Name] = i);
            v2Prog.Implementations.Iter(i => v2Impls[i.Name] = i);

            var implMap = new Dictionary<string, string>(); //(foo,foo)
            v1Impls.Keys.Iter(x => implMap[x] = x);
            v2Impls.Keys.Iter(x => implMap[x] = x); //add any additional procedures from v2

            //TODO: ignoring the config file now since v1v2Config.GetProcedureDictionary()
            //TODO: need to strip off the prefix from each of the entity (v1.Foo, v2.Foo)
            

            var diffImplPairs = new HashSet<Tuple<Implementation, Implementation>>();
            foreach (var pair in implMap)
            {
                Implementation i1, i2;
                var i1Present = v1Impls.TryGetValue(pair.Key, out i1);
                var i2Present = v2Impls.TryGetValue(pair.Value, out i2);

                if (i1Present && i2Present) //both impls present
                {
                    //TODO: get the string representation of the implementations 
                    if (IsEqualStringRepresentation(i1,i2)) continue; //exclude (i1, i2) from consideration safely
                    diffImplPairs.Add(Tuple.Create(i1, i2)); continue;
                } else if (!i1Present && !i2Present) //both are stubs
                {
                    continue;
                } else if (i1Present && !i2Present) //i2 missing
                {
                    diffImplPairs.Add(Tuple.Create<Implementation,Implementation>(i1, null));
                } else if (!i1Present && i2Present) //i1 missing
                {
                    diffImplPairs.Add(Tuple.Create<Implementation, Implementation>(null, i2));
                }
            }
            return diffImplPairs;
        }
        /// <summary>
        /// Returns true if i1 and i2 have identical bodies in Boogie level
        /// </summary>
        /// <param name="i1"></param>
        /// <param name="i2"></param>
        /// <returns></returns>
        private static bool IsEqualStringRepresentation(Implementation i1, Implementation i2)
        {
            return false;
        }
        /// <summary>
        /// Get a string representation of an implementation
        /// </summary>
        /// <param name="i1"></param>
        /// <returns></returns>
        private static object GetImplString(Implementation i1)
        {
            throw new NotImplementedException();
        }
        /// <summary>
        /// MEF based diffing of strings
        /// Doesn't work due to CFEditor dependency
        /// </summary>
        /// <param name="args"></param>

    }

    class RemoveSrcInfoStmts : FixedVisitor
    {
        public override Cmd VisitAssertCmd(AssertCmd node)
        {
            if (SDiff.Util.IsSourceInfoAssertCmd(node))
            {
                node.Attributes = null; 
            }
            return base.VisitAssertCmd(node);
        }
    }

    /// <summary>
    /// Manages the source code information for a program
    /// </summary>
    class SourceInfoManager
    {
        Program program;
        HashSet<string> srcFiles; 
        Dictionary<Implementation, Tuple<string, Tuple<int, int>>> srcInfoPerImpl; //impl -> (src-file, (min,max))
        Dictionary<Implementation, List<string>> srcLinesPerImpl; //impl -> srcLines 
        private string bplPath;
        public SourceInfoManager(Program prog, string bplPath)
        {
            program = prog;
            this.bplPath = bplPath;
            srcFiles = new HashSet<string>();
            srcInfoPerImpl = new Dictionary<Implementation, Tuple<string, Tuple<int, int>>>();
            srcLinesPerImpl = new Dictionary<Implementation, List<string>>();
        }
        /// <summary>
        /// Extracts the sourcefile/line information and the boundary of each implementation
        /// </summary>
        public void ComputeSourceInfoForImplementations()
        {
            foreach (var impl in program.Implementations)
            {
                string srcFile = null;
                List<int> lines = new List<int>();
                impl.Blocks
                    .Iter(b =>
                    {
                        b.Cmds.Where(c => SDiff.Util.IsSourceInfoAssertCmd(c))
                            .Iter(ac =>
                            {
                                int srcLine;
                                SDiff.Util.IsSourceInfoAssertCmd(ac, out srcFile, out srcLine);
                                srcFiles.Add(srcFile);
                                lines.Add(srcLine);
                            });
                    });
                lines.Sort(); //get the min and max lines
                int min = -1, max = -1;
                if (lines.Count() > 0)
                {
                    min = lines[0];
                    max = lines[lines.Count() - 1]; 
                }
                srcInfoPerImpl[impl] = Tuple.Create<string, Tuple<int, int>>(srcFile, Tuple.Create(min, max));
            }
            foreach(var src in srcFiles)
            {
                var contentSrc = new List<string>();
                using (var srcStream = new StreamReader(Path.Combine(bplPath, src)))
                {
                    while (srcStream.Peek() >= 0) { contentSrc.Add(srcStream.ReadLine()); }
                }
                var implsInSrc = program.Implementations.Where(i => srcInfoPerImpl[i].Item1 == src);
                foreach(var impl in implsInSrc)
                {
                    srcLinesPerImpl[impl] = new List<string>();
                    var info = srcInfoPerImpl[impl];
                    for (int i = info.Item2.Item1; i < info.Item2.Item2; ++i)
                        srcLinesPerImpl[impl].Add(contentSrc[i-1]);
                }
            }
        }
        public List<string> GetSrcLinesForImpl(Implementation i)
        {
            if (i == null || !srcLinesPerImpl.ContainsKey(i)) return new List<string>();  //impl is not present
            return srcLinesPerImpl[i];
        }
        public int GetStartLineForImplInFile(Implementation i)
        {
            Debug.Assert(i != null && srcInfoPerImpl.ContainsKey(i));
            return srcInfoPerImpl[i].Item2.Item1;
        }
    }

    /// <summary>
    /// This class should only care about strings  
    /// </summary>
    class DiffUsingTFS
    {

        public DiffUsingTFS() { }
        public DiffUsingTFS(string[] args)
        {
            Debug.Assert(args.Count() >= 2, "Usage: SyntaxDiff.exe file1 file2");
            string file1 = args[0], file2 = args[1];
            DiffOptions diffOptions = new DiffOptions();
            diffOptions.UseThirdPartyTool = false;
            diffOptions.OutputType = DiffOutputType.Unified;

            // Wherever we want to send our text-based diff output 
            diffOptions.StreamWriter = new System.IO.StreamWriter(Console.OpenStandardOutput());

            Console.WriteLine("Difference.DiffFiles - output to console");
            DiffSegment diffs = Microsoft.TeamFoundation.VersionControl.Client.Difference.DiffFiles(
                file1, FileType.Detect(file1, null), file2, FileType.Detect(file2, null),  diffOptions);

            var diff = diffs;
            while (diff != null)
            {
                Console.WriteLine("Diff ==> {0} {1}:{2}:{3} {4}:{5}:{6}", 
                    diff.Type, diff.OriginalStart, diff.OriginalLength, diff.OriginalStartOffset, diff.ModifiedStart, diff.ModifiedLength, diff.ModifiedStartOffset);
                diff = diff.Next;
            }
        }

        /// <summary>
        /// returns a list of line numbers in s and t that differ
        /// </summary>
        /// <param name="s"></param>
        /// <param name="t"></param>
        /// <returns></returns>
        public Tuple<List<int>, List<int>> DiffStrings(List<string> s, List<string> t)
        {
            if (s.Count == t.Count && s.Zip(t).Where(x => x.Item1 != x.Item2).Count() == 0)
            {
                Console.WriteLine("No diffs");
                return Tuple.Create(new List<int>(), new List<int>());
            }

            //dump to a file and call DiffFiles
            var sFileName = "___tmp_diff_str_1";
            var tFileName = "___tmp_diff_str_2";
            var sFile = new StreamWriter(sFileName);
            var tFile = new StreamWriter(tFileName);
            s.Iter(l => sFile.WriteLine(l)); sFile.Close();
            t.Iter(l => tFile.WriteLine(l)); tFile.Close();

            DiffOptions diffOptions = new DiffOptions();
            diffOptions.UseThirdPartyTool = false;
            diffOptions.OutputType = DiffOutputType.Unified;

            // Wherever we want to send our text-based diff output 
            diffOptions.StreamWriter = new System.IO.StreamWriter(Console.OpenStandardOutput());

            DiffSegment diffs = Microsoft.TeamFoundation.VersionControl.Client.Difference.DiffFiles(
                sFileName, FileType.Detect(sFileName, null), tFileName, FileType.Detect(tFileName, null), diffOptions);

            var diff = diffs;
            var sDiffLines = new List<int>();
            var tDiffLines = new List<int>();
            if (diff.Next != null) //some diff
            {
                int sLast = int.MaxValue;
                int tLast = int.MaxValue;
                while (diff != null)
                {
                    /*Console.WriteLine("Diff ==> {0} {1}:{2}:{3} {4}:{5}:{6}",
                        diff.Type, diff.OriginalStart, diff.OriginalLength, diff.OriginalStartOffset, diff.ModifiedStart, diff.ModifiedLength, diff.ModifiedStartOffset);*/
                    sDiffLines.AddRange(ComputeRange(sLast, diff.OriginalStart));
                    sLast = diff.OriginalStart + diff.OriginalLength;
                    tDiffLines.AddRange(ComputeRange(tLast, diff.ModifiedStart));
                    tLast = diff.ModifiedStart + diff.ModifiedLength;
                    diff = diff.Next;
                }
            }
            return Tuple.Create(sDiffLines, tDiffLines);
        }

        private IEnumerable<int> ComputeRange(int start, int end)
        {
            while (start < end)
            {
                yield return start;
                start++;
            }
        }
    }

    class DiffAnalyzerUsingMEFAndVisualStudio
    {
  
        public DiffAnalyzerUsingMEFAndVisualStudio() 
        {
            LoadMEFComponents();

            //var a = contentTypeRegistryService.ContentTypes;
            //IContentType b;

            differenceService = differencingServiceSelector.GetTextDifferencingService(null);
        }
        public void LoadMEFComponents()
        {
            string[] ComponentDllFilters = new string[] { "Microsoft.VisualStudio.CoreUtility.dll", /*"CFEditor.dll",*/ "Delta.dll", "Microsoft.VisualStudio.Text*dll" /*, "Microsoft.VisualStudio.Diagram*dll"*/ };
            /* this is lame and needs to be set for the machine that it is running on
             * There is probably a way to autodetect this, but I don't know it */

            //string componentsDir = "./";// ConfigurationManager.AppSettings["ComponentsDIR"];
            string executionDirectory = Path.GetDirectoryName(Assembly.GetEntryAssembly() == null ? Assembly.GetExecutingAssembly().Location : Assembly.GetEntryAssembly().Location);
            string componentsDir = executionDirectory;

            AggregateCatalog catalog = new AggregateCatalog();
            foreach (string filter in ComponentDllFilters)
            {
                catalog.Catalogs.Add(new DirectoryCatalog(componentsDir, filter));
            }

            try
            {
                CompositionBatch batch = new CompositionBatch();
                batch.AddPart(this);

                CompositionContainer container = new CompositionContainer(catalog, isThreadSafe: true);
                container.Compose(batch);
            }
            catch (ReflectionTypeLoadException ex)
            {
                foreach (Exception loaderException in ex.LoaderExceptions)
                {
                    Debug.WriteLine(loaderException.ToString());
                }
                throw;
            }
        }

        [Import]
        public ITextDifferencingSelectorService differencingServiceSelector = null;
        [Import]
        public IContentTypeRegistryService contentTypeRegistryService = null;        
        ITextDifferencingService differenceService = null;
        //[Import]
        //ITextBufferFactoryService textBufferFactoryService = null;

        //[Import]
        //IDifferenceBufferFactoryService differenceBufferFactoryService = null;
        public HashSet<Tuple<int, int>> PerformDiffFiles(string file1, string file2)
        {
            HashSet<Tuple<int, int>> diffRanges = new HashSet<Tuple<int, int>>();
            StringDifferenceOptions sdo = new StringDifferenceOptions()
            {
                DifferenceType = StringDifferenceTypes.Line | StringDifferenceTypes.Word,
                IgnoreTrimWhiteSpace = false
            };

            var origContent = (new StreamReader(file1)).ReadToEnd();
            var modifiedContent = (new StreamReader(file2)).ReadToEnd();
            var diffs = differenceService.DiffStrings(origContent, modifiedContent, sdo);
            var diffList = new List<CCDifference>();
            foreach (var d in diffs)
            {
                diffList.Add(new CCDifference(d.Left, d.Right, d.Before, d.After));
                diffRanges.Add(Tuple.Create<int, int>(d.Left.Start, d.Left.End));
            }
            return diffRanges;
        }
        public HashSet<Tuple<int, int>> PerformDiffString(string origContent, string modifiedContent)
        {
            HashSet<Tuple<int, int>> diffRanges = new HashSet<Tuple<int, int>>();
            StringDifferenceOptions sdo = new StringDifferenceOptions()
            {
                DifferenceType = StringDifferenceTypes.Line | StringDifferenceTypes.Word,
                IgnoreTrimWhiteSpace = false
            };

            var diffs = differenceService.DiffStrings(origContent, modifiedContent, sdo);
            var diffList = new List<CCDifference>();
            foreach (var d in diffs)
            {
                diffList.Add(new CCDifference(d.Left, d.Right, d.Before, d.After));
                diffRanges.Add(Tuple.Create<int, int>(d.Left.Start, d.Left.End));
            }
            return diffRanges;
        }
    }
}
