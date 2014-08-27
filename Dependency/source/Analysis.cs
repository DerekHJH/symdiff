﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Boogie;
using Microsoft.Boogie.GraphUtil;
using System.Collections;
using System.Diagnostics;

namespace Dependency
{
    class Analysis
    {
        static private class CmdLineOptsNames
        {
            public const string semanticDep = "/semanticDependency";
            public const string stats = "/stats";
            public const string prune = "/prune";
            public const string both = "/both";
            public const string dataOnly = "/dataOnly";
            public const string taint = "/taint";
            public const string debug = "/break";
            public const string detStubs = "/detStubs";
            public const string refine = "/refine";
            public const string readSet = "/readSet";
            public const string noMinUnsatCore = "/noMinUnsatCore";
            public const string timeout = "/timeout";
            public const string abstractNonTainted = "/abstractNonTainted"; //generate a program with non-tainted parts abstracted
            public const string splitMapsWithAliasAnalysis = "/splitMapsWithAliasAnalysis";
        }

        static public bool DataOnly = false;
        static public bool BothDependencies = false;
        static public bool Prune = false;
        static public bool PrintStats = false;
        static public bool DetStubs = false;
        static public bool Refine = false;
        public static bool SemanticDep = false;
        public static bool ReadSet = false;
        static public int StackBound = 3;
        static public bool noMinUnsatCore = false;
        static public int Timeout = 1000;
        static public bool AbstractNonTainted = false;
        static public bool SplitMapsWithAliasAnalysis = false;
        
        static private List<Tuple<string, string, int>> changeLog = new List<Tuple<string, string, int>>();
        static private List<Tuple<string, string, int>> taintLog = new List<Tuple<string, string, int>>();
        static private List<Tuple<string, string, int, string>> dependenciesLog = new List<Tuple<string, string, int, string>>();
        static private List<Tuple<string, string, Procedure, Variable, HashSet<Variable>>> statsLog = new List<Tuple<string, string, Procedure, Variable, HashSet<Variable>>>();
        static private string statsFile;

        

        static private Program program;

        static private Stopwatch sw;

        static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                Usage();
                return -1;
            }

            sw = new Stopwatch();
            sw.Start();

            CommandLineOptions.Install(new CommandLineOptions());
            CommandLineOptions.Clo.RunningBoogieFromCommandLine = true;
            var boogieOptions = "/typeEncoding:m -timeLimit:20 -removeEmptyBlocks:0 /printModel:1 /printModelToFile:model.dmp /printInstrumented "; // /errorLimit:1";
            //IMPORTANT: need the next to avoid crash while creating prover
            CommandLineOptions.Clo.Parse(boogieOptions.Split(' '));
            //IMPORTANT: need these two to make use of UNSAT cores!!
            CommandLineOptions.Clo.UseUnsatCoreForContractInfer = true; //ROHIT
            CommandLineOptions.Clo.ContractInfer = true; //ROHIT

            #region Command line parsing 
            statsFile = args[0] + ".csv";

            string changeList = null;
            args.Where(x => x.StartsWith(CmdLineOptsNames.taint + ":"))
                .Iter(s => changeList = s.Split(':')[1]);

            DataOnly = args.Any(x => x.ToLower() == CmdLineOptsNames.dataOnly.ToLower());
            BothDependencies = args.Any(x => x.ToLower() == CmdLineOptsNames.both.ToLower()) && !DataOnly;
            DetStubs = args.Any(x => x.ToLower() == CmdLineOptsNames.detStubs.ToLower());

            PrintStats = args.Any(x => x.ToLower() == CmdLineOptsNames.stats.ToLower() || x.StartsWith(CmdLineOptsNames.stats + ":"));
            args.Where(x => x.StartsWith(CmdLineOptsNames.stats + ":"))
                .Iter(s => statsFile = s.Split(':')[1]);

            SemanticDep = args.Any(x => x.Contains(CmdLineOptsNames.semanticDep));

            Refine = args.Any(x => x == CmdLineOptsNames.refine || x.StartsWith(CmdLineOptsNames.refine + ":"));
            args.Where(x => x.StartsWith(CmdLineOptsNames.refine + ":"))
                .Iter(s => StackBound = int.Parse(s.Split(':')[1]));

            args.Where(x => x.StartsWith(CmdLineOptsNames.timeout + ":"))
                .Iter(s => Timeout = int.Parse(s.Split(':')[1]));

            SplitMapsWithAliasAnalysis = args.Any(x => x.StartsWith(CmdLineOptsNames.splitMapsWithAliasAnalysis));

            // refined must have pruned dependencies
            Prune = Refine || args.Any(x => x.ToLower() == CmdLineOptsNames.prune.ToLower());

            if (StackBound < 2)
                throw new Exception("Argument k to /refine:k has to be > 1");

            ReadSet = args.Any(x => x.ToLower() == CmdLineOptsNames.readSet.ToLower());

            noMinUnsatCore = args.Any(x => x.ToLower() == CmdLineOptsNames.noMinUnsatCore.ToLower());

            AbstractNonTainted = args.Any(x => x.ToLower() == CmdLineOptsNames.abstractNonTainted.ToLower());

            if (args.Any(x => x.ToLower() == CmdLineOptsNames.debug.ToLower()))
                Debugger.Launch();
            #endregion 


            var filename = args[0];
            if (!Utils.ParseProgram(filename, out program))
            {
                Usage();
                return -1;
            }

            //cleanup assume value_is, as we are not printing a trace now
            (new Utils.RemoveValueIsAssumes()).Visit(program);

            if (SplitMapsWithAliasAnalysis)
            {
                var s = new SplitHeapUsingAliasAnalysis(program, filename);
                s.Run();
                return 0;
            }

            Utils.CallGraphHelper.WriteCallGraph(filename + ".cg.dot", Utils.CallGraphHelper.ComputeCallGraph(program));

            if (changeList != null) PopulateChangeLog(changeList,program);
            RunAnalysis(filename, program);


            #region Display and Log
            var displayHtml = new Utils.DisplayHtmlHelper(changeLog, taintLog, dependenciesLog);
            displayHtml.GenerateHtmlOutput(filename + ".html");
            Console.WriteLine("Output generated in " + filename + ".html");

            if (PrintStats)
            {
                Utils.StatisticsHelper.GenerateCSVOutput(statsFile, statsLog);
                Console.WriteLine("Statistics generated in " + statsFile);
            }

            // print number of tainted lines
            Console.WriteLine("#Tainted:\n {0}", taintLog.GroupBy(t => t.Item3).Count());

            #endregion
            sw.Stop();
            return 0;
        }

        public static void PopulateChangeLog(string changelist, Program program)
        {
            if (changelist == null)
                return;
            StreamReader reader = File.OpenText(changelist);
            string line;
            while ((line = reader.ReadLine()) != null)
            {
                try  
                {
                    string[] items = line.Split(',');
                    string procName = items[0].Trim(), procFile = null;
                    // locate the source file for the procedure
                    var impl = program.Implementations().Single(i => i.Name == procName);
                    impl.Blocks.Find(b => b.Cmds.Count > 0 && b.Cmds[0] is AssertCmd && (procFile = Utils.AttributeUtils.GetSourceFile(b)) != null);
                    changeLog.Add(Tuple.Create(procFile, procName, int.Parse(items[1])));
                }
                catch (Exception)
                {
                    Console.WriteLine("Warning: Ignoring misformed/empty line in {0}: {1}",changelist,line);
                    continue;
                }
            }
        }

        static public void PopulateDependencyLog(Implementation impl, Dependencies deps, string which)
        {
            if (impl == null) return; //if this is a stub
            var proc = impl.Proc;
            string sourcefile = Utils.AttributeUtils.GetImplSourceFile(impl);
            var sourceLines = impl.Blocks.Where(b => b.Cmds.Count > 0 && b.Cmds[0] is AssertCmd).Select(b => Utils.AttributeUtils.GetSourceLine(b));
            if (sourceLines.Count() == 0)
                return;
            int lastSourceLine = sourceLines.Max();

            string depStr = "<b> " + which + " for " + proc.Name + "(): (Size = " + deps.Sum(d => d.Value.Count) + ")</b> " + deps.ToString();
            dependenciesLog.Add(new Tuple<string, string, int, string>(sourcefile, proc.Name, lastSourceLine, depStr));
        }

        static public void PopulateTaintLog(Implementation node, IEnumerable<Block> taintedBlocks)
        {
            string sourcefile = Utils.AttributeUtils.GetImplSourceFile(node);
            foreach (var block in taintedBlocks)
            {
                int sourceline = Utils.AttributeUtils.GetSourceLine(block);
                if (sourceline >= 0)
                    taintLog.Add(new Tuple<string, string, int>(sourcefile, node.Proc.Name, sourceline));
            }
        }

        public static void PopulateStatsLog(string type, Implementation impl, Variable key, HashSet<Variable> value)
        {
            statsLog.Add(new Tuple<string,string, Procedure, Variable,HashSet<Variable>>(type,Utils.AttributeUtils.GetImplSourceFile(impl), impl.Proc, key, value));
        }

        private static void RunAnalysis(string filename, Program program)
        {
            var dataDepVisitor = new DependencyVisitor(filename, program, changeLog, true, DetStubs);
            var dataDeps = dataDepVisitor.ProcDependencies;
            
            if (Refine || BothDependencies || DataOnly) {
                RunDependencyAnalysis(program, dataDepVisitor, Utils.StatisticsHelper.DataOnly, DataOnly);
                if (DataOnly)
                    return;
            }

            dataDepVisitor.worklist.stateSpace.Clear(); // helping the garbage collector
            GC.Collect();

            var allDepVisitor = new DependencyVisitor(filename, program, changeLog, DataOnly, DetStubs);
            var allDeps = allDepVisitor.ProcDependencies;

            if (Refine || !ReadSet)
                RunDependencyAnalysis(program, allDepVisitor, Utils.StatisticsHelper.DataAndControl, !ReadSet); // !ReadSet in the case we want to compute taint using the read set as the baseline dependency

            //// test SB deps
            //Random rnd = new Random();
            //foreach (var impl in program.Implementations())
            //{
            //    Console.WriteLine("Deps[" + impl + "] = " + allDeps[impl.Proc]);
            //    Console.WriteLine("Superblock = " + Utils.DependenciesUtils.SuperBlockDependencies(impl.Blocks, allDepVisitor.worklist.stateSpace[impl.Blocks.Last().TransferCmd], allDeps));
            //    int start = rnd.Next(0, impl.Blocks.Count);
            //    int num = rnd.Next(1, impl.Blocks.Count - start);
            //    var superBlock = impl.Blocks.GetRange(start, num);
            //    var exitBlock = superBlock.Last();
            //    if (!allDepVisitor.worklist.stateSpace.ContainsKey(exitBlock.TransferCmd))
            //    {
            //        Console.WriteLine("Block " + impl.Blocks[start + num] + " not in statspace");
            //        Debug.Assert(false);
            //    }
            //    var deps = Utils.DependenciesUtils.SuperBlockDependencies(superBlock, allDepVisitor.worklist.stateSpace[exitBlock.TransferCmd], allDeps);
            //    Console.Write("Deps for [");
            //    impl.Blocks.GetRange(start, num).Iter(b => Console.Write(b + ","));
            //    Console.Write("]:");
            //    Console.WriteLine(deps);
            //}

            allDepVisitor.worklist.stateSpace.Clear(); // helping the garbage collector
            GC.Collect();

            #region Control+Data dependencies must contain the Data dependencies
            Debug.Assert(dataDeps.All(pd => { 
                var proc = pd.Key; var ddeps = pd.Value; var adeps = allDeps[proc];
                return ddeps.Keys.All(v => {
                    if (!adeps.Keys.Contains(v)) {
                        Console.WriteLine("{2}: {0} not contained in {1}",v,adeps.ToString(),proc);
                        return false;
                    }
                    if (!adeps[v].IsSupersetOf(ddeps[v])) {
                        Console.WriteLine("{3}: {0} not superset of {1} for {2}", adeps.ToString(), ddeps.ToString(), v, proc);
                        return false;
                    }
                    return true;
                });          
            }));
            #endregion

            ProcReadSetVisitor rsVisitor = new ProcReadSetVisitor();
            if (ReadSet)
            {
                RunReadSetAnalysis(program, rsVisitor, new DependencyVisitor(filename, program, changeLog, DataOnly, DetStubs));
                #region ReadSet must contain the Control+Data dependencies
                Debug.Assert(rsVisitor.ProcReadSet.All(prs =>
                {
                    var proc = prs.Key; var readSet = prs.Value;
                    if (!allDeps.ContainsKey(proc)) return true;
                    var deps = allDeps[proc];
                    return deps.Keys.All(v => {
                        if (!(readSet.Contains(v) && readSet.IsSupersetOf(deps[v])))
                        {
                            Console.WriteLine("Failed for " + v + " in proc " + proc);
                            Console.WriteLine("RS:");
                            readSet.Iter(r => Console.WriteLine(" " + r));
                            Console.WriteLine("Deps[" + v +"]:");
                            deps[v].Iter(r => Console.WriteLine(" " + r));

                            return false;
                        }
                        return true;
                    });
                }));
                #endregion
            }


            if (Refine)
                RunRefinedDepAnalysis(filename, program, dataDeps, allDeps);

            // TODO: create tainted blocks and block dependencies
            if (AbstractNonTainted)
            {
                Dictionary<Procedure, List<Block>> taintedBlocks = new Dictionary<Procedure, List<Block>>();
                Dictionary<Block, Dependencies> blockDeps = new Dictionary<Block, Dependencies>();
                AbstractedTaint.CreateAbstractedTaintProgram(program, allDeps, blockDeps, taintedBlocks);
            }            
        }

        private static void RunDependencyAnalysis(Program program, DependencyVisitor visitor, string kind, bool printTaint = false)
        {
            visitor.Visit(program);
            var deps = visitor.ProcDependencies;

            if (printTaint && changeLog.Count > 0)
                // extract taint from dependencies and print
                program.Implementations().Iter(impl => PopulateTaintLog(impl, Utils.ExtractTaint(visitor)));

            if (Prune)
                Utils.DependenciesUtils.PruneProcDependencies(program, deps);

            program.Implementations().Iter(impl => PopulateDependencyLog(impl, deps[impl.Proc], kind));

            if (changeLog.Count > 0)
                // remove the special taint var
                deps.Values.Iter(dep => dep.Values.Iter(d => { d.Remove(Utils.VariableUtils.BottomUpTaintVar); d.Remove(Utils.VariableUtils.TopDownTaintVar); }));

            if (PrintStats)
                program.Implementations().Iter(impl => deps[impl.Proc].Iter(dep => PopulateStatsLog(kind, impl, dep.Key, dep.Value)));
        }

        private static void RunRefinedDepAnalysis(string filename, Program program, Dictionary<Procedure, Dependencies> lowerBound, Dictionary<Procedure, Dependencies> upperBound)
        {
            var refineDepsWL = new RefineDependencyWL(filename, program, lowerBound, upperBound, StackBound);
            Utils.LogStopwatch(sw, "Initial analysis", Analysis.Timeout);
            refineDepsWL.RunFixedPoint(sw);
            Utils.LogStopwatch(sw, "After refined dependency analysis", Analysis.Timeout);
            // print
            refineDepsWL.currDependencies.Iter(pd => PopulateDependencyLog(program.Implementations().SingleOrDefault(i => i.Proc.Name == pd.Key.Name), pd.Value, Utils.StatisticsHelper.Refined));

            // stats
            refineDepsWL.currDependencies.Iter(pd =>
            {
                var impl = program.Implementations().SingleOrDefault(i => i.Proc.Name == pd.Key.Name);
                if (impl != null)
                    pd.Value.Iter(dep => PopulateStatsLog(Utils.StatisticsHelper.Refined, impl, dep.Key, dep.Value));
            });
        }

        private static void RunReadSetAnalysis(Program program, ProcReadSetVisitor rsVisitor, DependencyVisitor depVisitor = null)
        {
            rsVisitor.Visit(program);
            // prune
            if (Prune)
                rsVisitor.ProcReadSet.Keys.Iter(p => Utils.VariableUtils.PruneLocals(program.Implementations().SingleOrDefault(i => i.Proc.Name == p.Name), rsVisitor.ProcReadSet[p]));

            // create a dependency set \foreach r \in ReadSet: r <- ReadSet
            Dictionary<Procedure,Dependencies> rsProcDeps = new Dictionary<Procedure,Dependencies>();
            rsVisitor.ProcReadSet.Keys.Iter(p => { rsProcDeps[p] = new Dependencies(); rsVisitor.ProcReadSet[p].Iter(r => rsProcDeps[p][r] = rsVisitor.ProcReadSet[p]); });

            // taint
            if (changeLog.Count > 0)
            {
                depVisitor.ProcDependencies = rsProcDeps;
                depVisitor.Visit(program); // reminder: taint is essentially a dependecy analysis
                // extract taint from dependencies and print
                program.Implementations().Iter(impl => PopulateTaintLog(impl, Utils.ExtractTaint(depVisitor)));
                // remove the special taint var
                rsProcDeps.Values.Iter(dep => dep.Values.Iter(d => { d.Remove(Utils.VariableUtils.BottomUpTaintVar); d.Remove(Utils.VariableUtils.TopDownTaintVar); }));
            }

            // print
            //program.Implementations().Iter(impl => PopulateDependencyLog(impl, rsProcDeps[impl.Proc], Utils.StatisticsHelper.ReadSet));


            // stats
            if (PrintStats)
                rsVisitor.ProcReadSet.Iter(prs =>
                {
                    var proc = prs.Key; var readSet = prs.Value;
                    var impl = program.Implementations().SingleOrDefault(i => i.Proc.Name == proc.Name);
                    if (impl != null) // conservatively each output\global is dependent on all of the readset
                        readSet.Where(v => v is GlobalVariable || proc.OutParams.Contains(v)).Iter(v => PopulateStatsLog(Utils.StatisticsHelper.ReadSet, impl, v, readSet));
                });

        }



        private static void Usage()
        {
            int length = 25;
            string execName = System.Diagnostics.Process.GetCurrentProcess().MainModule.ModuleName;
            Console.WriteLine("Lightweight inter-procedural dependency\\taint analysis for change impact");
            Console.WriteLine("Usage: " + execName + " <filename.bpl> [flags]");
            Console.WriteLine((CmdLineOptsNames.taint + ":changelist.txt").PadRight(length,' ') + " - produce taint for all lined marked as changed in changelist.txt");
            Console.WriteLine(CmdLineOptsNames.dataOnly.PadRight(length,' ') + " - compute data dependnecies\\taint only (no control)");
            Console.WriteLine(CmdLineOptsNames.both.PadRight(length,' ') + " - compute both data & control dependencies for showing in HTML");
            Console.WriteLine(CmdLineOptsNames.prune.PadRight(length,' ') + " - show only in\\out\\global dependencies (no locals)");
            Console.WriteLine((CmdLineOptsNames.stats + "[:statsfile.csv]").PadRight(length,' ') +  " - print dependecies statistics in CSV format [to a specified file]");
            Console.WriteLine(CmdLineOptsNames.detStubs.PadRight(length, ' ') + " - (unsoundly) assume stub functions to depend only on input (and not be undeterministic)");
            Console.WriteLine(CmdLineOptsNames.readSet.PadRight(length, ' ') + " - compute a naive read set (for comparison purposes)");
            Console.WriteLine((CmdLineOptsNames.refine + "[:<int>]").PadRight(length, ' ') + " - refine result [with inlining up to stack bound]");
        }


    }
}
