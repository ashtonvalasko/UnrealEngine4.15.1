using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnrealBuildTool;
using BuildGraph;
using System.Reflection;
using System.Collections;
using System.IO;

namespace AutomationTool
{
	/// <summary>
	/// Tool to execute build processes for UE4 projects, which can be run locally or in parallel across a build farm (assuming synchronization and resource allocation implemented by a separate system).
	///
	/// Build graphs are declared using an XML script using syntax similar to MSBuild, ANT or NAnt, and consist of the following components:
	///
	/// - Tasks:        Building blocks which can be executed as part of the build process. Many predefined tasks are provided ('Cook', 'Compile', 'Copy', 'Stage', 'Log', 'PakFile', etc...), and additional tasks may be 
	///                 added be declaring classes derived from AutomationTool.CustomTask in other UAT modules. 
	/// - Nodes:        A named sequence of tasks which are executed in order to produce outputs. Nodes may have dependencies on other nodes for their outputs before they can be executed. Declared with the 'Node' element.
	/// - Agents:		A machine which can execute a sequence of nodes, if running as part of a build system. Has no effect when building locally. Declared with the 'Agent' element.
	/// - Triggers:     Container for agents which should only be executed when explicitly triggered (using the -Trigger=... or -SkipTriggers command line argument). Declared with the 'Trigger' element.
	/// - Notifiers:    Specifies email recipients for failures in one or more nodes, whether they should receive notifications on warnings, and so on.
	/// 
	/// Properties can be passed in to a script on the command line, or set procedurally with the &ltProperty Name="Foo" Value="Bar"/&gt; syntax. Properties referenced with the $(Property Name) notation are valid within 
	/// all strings, and will be expanded as macros when the script is read. If a property name is not set explicitly, it defaults to the contents of an environment variable with the same name. 
	/// Local properties, which only affect the scope of the containing XML element (node, agent, etc...) are declared with the &lt;Local Name="Foo" Value="Bar"/&gt; element, and will override a similarly named global 
	/// property for the local property's scope.
	///
	/// Any elements can be conditionally defined via the "If" attribute, which follows a syntax similar to MSBuild. Literals in conditions may be quoted with single (') or double (") quotes, or an unquoted sequence of 
	/// letters, digits and underscore characters. All literals are considered identical regardless of how they are declared, and are considered case-insensitive for comparisons (so true equals 'True', equals "TRUE"). 
	/// Available operators are "==", "!=", "And", "Or", "!", "(...)", "Exists(...)" and "HasTrailingSlash(...)". A full grammar is written up in Condition.cs.
	/// 
	/// File manipulation is done using wildcards and tags. Any attribute that accepts a list of files may consist of: a Perforce-style wildcard (matching any number of "...", "*" and "?" patterns in any location), a 
	/// full path name, or a reference to a tagged collection of files, denoted by prefixing with a '#' character. Files may be added to a tag set using the &lt;Tag&gt; Task, which also allows performing set union/difference 
	/// style operations. Each node can declare multiple outputs in the form of a list of named tags, which other nodes can then depend on.
	/// 
	/// Build graphs may be executed in parallel as part build system. To do so, the initial graph configuration is generated by running with the -Export=... argument (producing a JSON file listing the nodes 
	/// and dependencies to execute). Each participating agent should be synced to the same changelist, and UAT should be re-run with the appropriate -Node=... argument. Outputs from different nodes are transferred between 
	/// agents via shared storage, typically a network share, the path to which can be specified on the command line using the -SharedStorageDir=... argument. Note that the allocation of machines, and coordination between 
	/// them, is assumed to be managed by an external system based on the contents of the script generated by -Export=....
	/// 
	/// A schema for the known set of tasks can be generated by running UAT with the -Schema=... option. Generating a schema and referencing it from a BuildGraph script allows Visual Studio to validate and auto-complete 
	/// elements as you type.
	/// </summary>
	[Help("Tool for creating extensible build processes in UE4 which can be run locally or in parallel across a build farm.")]
	[Help("Script=<FileName>", "Path to the script describing the graph")]
	[Help("Target=<Name>", "Name of the node or output tag to be built")]
	[Help("Schema=<FileName>", "Generate a schema describing valid script documents, including all the known tasks")]
	[Help("Set:<Property>=<Value>", "Sets a named property to the given value")]
	[Help("Clean", "Cleans all cached state of completed build nodes before running")]
	[Help("CleanNode=<Name>[+<Name>...]", "Cleans just the given nodes before running")]
	[Help("ListOnly", "Shows the contents of the preprocessed graph, but does not execute it")]
	[Help("ShowDeps", "Show node dependencies in the graph output")]
	[Help("ShowNotifications", "Show notifications that will be sent for each node in the output")]
	[Help("Trigger=<Name>[+<Name>...]", "Activates the given triggers, including all the nodes behind them in the graph")]
	[Help("SkipTriggers", "Activate all triggers")]
	[Help("TokenSignature=<Name>", "Specifies the signature identifying the current job, to be written to tokens for nodes that require them. Tokens are ignored if this parameter is not specified.")]
	[Help("SkipTargetsWithoutTokens", "Excludes targets which we can't acquire tokens for, rather than failing")]
	[Help("Preprocess=<FileName>", "Writes the preprocessed graph to the given file")]
	[Help("Export=<FileName>", "Exports a JSON file containing the preprocessed build graph, for use as part of a build system")]
	[Help("PublicTasksOnly", "Only include built-in tasks in the schema, excluding any other UAT modules")]
	[Help("SharedStorageDir=<DirName>", "Sets the directory to use to transfer build products between agents in a build farm")]
	[Help("SingleNode=<Name>", "Run only the given node. Intended for use on a build system after running with -Export.")]
	[Help("WriteToSharedStorage", "Allow writing to shared storage. If not set, but -SharedStorageDir is specified, build products will read but not written")]
	public class BuildGraph : BuildCommand
	{
		/// <summary>
		/// Main entry point for the BuildGraph command
		/// </summary>
		public override ExitCode Execute()
		{
			// Parse the command line parameters
			string ScriptFileName = ParseParamValue("Script", null);
			if(ScriptFileName == null)
			{
				LogError("Missing -Script= parameter for BuildGraph");
				return ExitCode.Error_Unknown;
			}

			string TargetNames = ParseParamValue("Target", null);
			string SchemaFileName = ParseParamValue("Schema", null);
			string ExportFileName = ParseParamValue("Export", null);
			string PreprocessedFileName = ParseParamValue("Preprocess", null);
			string SharedStorageDir = ParseParamValue("SharedStorageDir", null);
			string SingleNodeName = ParseParamValue("SingleNode", null);
			string[] TriggerNames = ParseParamValue("Trigger", "").Split(new char[]{ '+', ';' }, StringSplitOptions.RemoveEmptyEntries).ToArray();
			bool bSkipTriggers = ParseParam("SkipTriggers");
			string TokenSignature = ParseParamValue("TokenSignature", null);
			bool bSkipTargetsWithoutTokens = ParseParam("SkipTargetsWithoutTokens");
			bool bClearHistory = ParseParam("Clean") || ParseParam("ClearHistory");
			bool bListOnly = ParseParam("ListOnly");
			bool bWriteToSharedStorage = ParseParam("WriteToSharedStorage") || CommandUtils.IsBuildMachine;
			bool bPublicTasksOnly = ParseParam("PublicTasksOnly");
			string ReportName = ParseParamValue("ReportName", null); 

			GraphPrintOptions PrintOptions = GraphPrintOptions.ShowCommandLineOptions;
			if(ParseParam("ShowDeps"))
			{
				PrintOptions |= GraphPrintOptions.ShowDependencies;
			}
			if(ParseParam("ShowNotifications"))
			{
				PrintOptions |= GraphPrintOptions.ShowNotifications;
			}

			// Parse any specific nodes to clean
			List<string> CleanNodes = new List<string>();
			foreach(string NodeList in ParseParamValues("CleanNode"))
			{
				foreach(string NodeName in NodeList.Split('+', ';'))
				{
					CleanNodes.Add(NodeName);
				}
			}

			// Set up the standard properties which build scripts might need
			Dictionary<string, string> DefaultProperties = new Dictionary<string,string>(StringComparer.InvariantCultureIgnoreCase);
			DefaultProperties["Branch"] = P4Enabled ? P4Env.BuildRootP4 : "Unknown";
			DefaultProperties["EscapedBranch"] = P4Enabled ? P4Env.BuildRootEscaped : "Unknown";
			DefaultProperties["Change"] = P4Enabled ? P4Env.Changelist.ToString() : "0";
			DefaultProperties["RootDir"] = CommandUtils.RootDirectory.FullName;
			DefaultProperties["IsBuildMachine"] = IsBuildMachine ? "true" : "false";
			DefaultProperties["HostPlatform"] = HostPlatform.Current.HostEditorPlatform.ToString();

			// Attempt to read existing Build Version information
			BuildVersion Version;
			if (BuildVersion.TryRead(FileReference.Combine(CommandUtils.RootDirectory, "Engine", "Build", "Build.version").FullName, out Version))
			{
				DefaultProperties["EngineMajorVersion"] = Version.MajorVersion.ToString();
				DefaultProperties["EngineMinorVersion"] = Version.MinorVersion.ToString();
				DefaultProperties["EnginePatchVersion"] = Version.PatchVersion.ToString();
			}

			// Add any additional custom arguments from the command line (of the form -Set:X=Y)
			Dictionary<string, string> Arguments = new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase);
			foreach (string Param in Params)
			{
				const string Prefix = "set:";
				if(Param.StartsWith(Prefix, StringComparison.InvariantCultureIgnoreCase))
				{
					int EqualsIdx = Param.IndexOf('=');
					if(EqualsIdx >= 0)
					{
						Arguments[Param.Substring(Prefix.Length, EqualsIdx - Prefix.Length)] = Param.Substring(EqualsIdx + 1);
					}
					else
					{
						LogWarning("Missing value for '{0}'", Param.Substring(Prefix.Length));
					}
				}
			}

			// Find all the tasks from the loaded assemblies
			Dictionary<string, ScriptTask> NameToTask = new Dictionary<string,ScriptTask>();
			if(!FindAvailableTasks(NameToTask, bPublicTasksOnly))
			{
				return ExitCode.Error_Unknown;
			}

			// Create a schema for the given tasks
			ScriptSchema Schema = new ScriptSchema(NameToTask);
			if(SchemaFileName != null)
			{
				Schema.Export(new FileReference(SchemaFileName));
			}

			// Read the script from disk
			Graph Graph;
			if(!ScriptReader.TryRead(new FileReference(ScriptFileName), Arguments, DefaultProperties, Schema, out Graph))
			{
				return ExitCode.Error_Unknown;
			}

			// Create the temp storage handler
			DirectoryReference RootDir = new DirectoryReference(CommandUtils.CmdEnv.LocalRoot);
			TempStorage Storage = new TempStorage(RootDir, DirectoryReference.Combine(RootDir, "Engine", "Saved", "BuildGraph"), (SharedStorageDir == null)? null : new DirectoryReference(SharedStorageDir), bWriteToSharedStorage);
			if(bClearHistory)
			{
				Storage.CleanLocal();
			}
			foreach(string CleanNode in CleanNodes)
			{
				Storage.CleanLocalNode(CleanNode);
			}

			// Convert the supplied target references into nodes 
			HashSet<Node> TargetNodes = new HashSet<Node>();
			if(TargetNames == null)
			{
				if(!bListOnly)
				{
					LogError("Missing -Target= parameter for BuildGraph");
					return ExitCode.Error_Unknown;
				}
				TargetNodes.UnionWith(Graph.Agents.SelectMany(x => x.Nodes));
			}
			else
			{
				foreach(string TargetName in TargetNames.Split(new char[]{ '+', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()))
				{
					Node[] Nodes;
					if(!Graph.TryResolveReference(TargetName, out Nodes))
					{
						LogError("Target '{0}' is not in graph", TargetName);
						return ExitCode.Error_Unknown;
					}
					TargetNodes.UnionWith(Nodes);
				}
			}

			// Try to acquire tokens for all the target nodes we want to build
			if(TokenSignature != null)
			{
				// Find all the lock files
				HashSet<FileReference> RequiredTokens = new HashSet<FileReference>(TargetNodes.SelectMany(x => x.RequiredTokens));

				// Try to create all the lock files
				List<FileReference> CreatedTokens = new List<FileReference>();
				if(!bListOnly)
				{
					CreatedTokens.AddRange(RequiredTokens.Where(x => WriteTokenFile(x, TokenSignature)));
				}

				// Find all the tokens that we don't have
				Dictionary<FileReference, string> MissingTokens = new Dictionary<FileReference, string>();
				foreach(FileReference RequiredToken in RequiredTokens)
				{
					string CurrentOwner = ReadTokenFile(RequiredToken);
					if(CurrentOwner != null && CurrentOwner != TokenSignature)
					{
						MissingTokens.Add(RequiredToken, CurrentOwner);
					}
				}

				// If we want to skip all the nodes with missing locks, adjust the target nodes to account for it
				if(MissingTokens.Count > 0)
				{
					if(bSkipTargetsWithoutTokens)
					{
						foreach(KeyValuePair<FileReference, string> Pair in MissingTokens)
						{
							List<Node> SkipNodes = TargetNodes.Where(x => x.RequiredTokens.Contains(Pair.Key)).ToList();
							Log("Skipping {0} due to previous build: {1}", String.Join(", ", SkipNodes), Pair.Value);
							TargetNodes.ExceptWith(SkipNodes);
						}
					}
					else
					{
						foreach(KeyValuePair<FileReference, string> Pair in MissingTokens)
						{
							List<Node> SkipNodes = TargetNodes.Where(x => x.RequiredTokens.Contains(Pair.Key)).ToList();
							LogError("Cannot run {0} due to previous build: {1}", String.Join(", ", SkipNodes), Pair.Value);
						}
						foreach(FileReference CreatedToken in CreatedTokens)
						{
							CreatedToken.Delete();
						}
						return ExitCode.Error_Unknown;
					}
				}
			}

			// Cull the graph to include only those nodes
			Graph.Select(TargetNodes);

			// If a report for the whole build was requested, insert it into the graph
			if (ReportName != null)
			{
				Report NewReport = new Report(ReportName);
				NewReport.Nodes.UnionWith(Graph.Agents.SelectMany(x => x.Nodes));
				Graph.NameToReport.Add(ReportName, NewReport);
			}

			// Write out the preprocessed script
			if (PreprocessedFileName != null)
			{
				Graph.Write(new FileReference(PreprocessedFileName), (SchemaFileName != null)? new FileReference(SchemaFileName) : null);
			}

			// Find the triggers which are explicitly activated, and all of its upstream triggers.
			HashSet<ManualTrigger> Triggers = new HashSet<ManualTrigger>();
			foreach(string TriggerName in TriggerNames)
			{
				ManualTrigger Trigger;
				if(!Graph.NameToTrigger.TryGetValue(TriggerName, out Trigger))
				{
					LogError("Couldn't find trigger '{0}'", TriggerName);
					return ExitCode.Error_Unknown;
				}
				while(Trigger != null)
				{
					Triggers.Add(Trigger);
					Trigger = Trigger.Parent;
				}
			}
			if(bSkipTriggers)
			{
				Triggers.UnionWith(Graph.NameToTrigger.Values);
			}

			// If we're just building a single node, find it 
			Node SingleNode = null;
			if(SingleNodeName != null && !Graph.NameToNode.TryGetValue(SingleNodeName, out SingleNode))
			{
				LogError("Node '{0}' is not in the trimmed graph", SingleNodeName);
				return ExitCode.Error_Unknown;
			}

			// If we just want to show the contents of the graph, do so and exit.
			if(bListOnly)
			{ 
				HashSet<Node> CompletedNodes = FindCompletedNodes(Graph, Storage);
				Graph.Print(CompletedNodes, PrintOptions);
				return ExitCode.Success;
			}

			// Print out all the diagnostic messages which still apply, unless we're running a step as part of a build system or just listing the contents of the file. 
			if(SingleNode == null)
			{
				IEnumerable<GraphDiagnostic> Diagnostics = Graph.Diagnostics.Where(x => x.EnclosingTrigger == null || Triggers.Contains(x.EnclosingTrigger));
				foreach(GraphDiagnostic Diagnostic in Diagnostics)
				{
					if(Diagnostic.EventType == LogEventType.Warning)
					{
						CommandUtils.LogWarning(Diagnostic.Message);
					}
					else
					{
						CommandUtils.LogError(Diagnostic.Message);
					}
				}
				if(Diagnostics.Any(x => x.EventType == LogEventType.Error))
				{
					return ExitCode.Error_Unknown;
				}
			}

			// Execute the command
			if(ExportFileName != null)
			{
				HashSet<Node> CompletedNodes = FindCompletedNodes(Graph, Storage);
				Graph.Print(CompletedNodes, PrintOptions);
				Graph.Export(new FileReference(ExportFileName), Triggers, CompletedNodes);
			}
			else if(SingleNode != null)
			{
				if(!BuildNode(new JobContext(this), Graph, SingleNode, Storage, bWithBanner: true))
				{
					return ExitCode.Error_Unknown;
				}
			}
			else
			{
				if(!BuildAllNodes(new JobContext(this), Graph, Storage))
				{
					return ExitCode.Error_Unknown;
				}
			}
			return ExitCode.Success;
		}

		/// <summary>
		/// Find all the tasks which are available from the loaded assemblies
		/// </summary>
		/// <param name="TaskNameToReflectionInfo">Mapping from task name to information about how to serialize it</param>
		/// <param name="bPublicTasksOnly">Whether to include just public tasks, or all the tasks in any loaded assemblies</param>
		static bool FindAvailableTasks(Dictionary<string, ScriptTask> NameToTask, bool bPublicTasksOnly)
		{
			Assembly[] LoadedAssemblies = AppDomain.CurrentDomain.GetAssemblies();
			if(bPublicTasksOnly)
			{
				LoadedAssemblies = LoadedAssemblies.Where(x => IsPublicAssembly(new FileReference(x.Location))).ToArray();
			}
			foreach (Assembly LoadedAssembly in LoadedAssemblies)
			{
				Type[] Types = LoadedAssembly.GetTypes();
				foreach(Type Type in Types)
				{
					foreach(TaskElementAttribute ElementAttribute in Type.GetCustomAttributes<TaskElementAttribute>())
					{
						if(!Type.IsSubclassOf(typeof(CustomTask)))
						{
							CommandUtils.LogError("Class '{0}' has TaskElementAttribute, but is not derived from 'Task'", Type.Name);
							return false;
						}
						if(NameToTask.ContainsKey(ElementAttribute.Name))
						{
							CommandUtils.LogError("Found multiple handlers for task elements called '{0}'", ElementAttribute.Name);
							return false;
						}
						NameToTask.Add(ElementAttribute.Name, new ScriptTask(ElementAttribute.Name, Type, ElementAttribute.ParametersType));
					}
				}
			}
			return true;
		}

		/// <summary>
		/// Reads the contents of the given token
		/// </summary>
		/// <returns>Contents of the token, or null if it does not exist</returns>
		public string ReadTokenFile(FileReference Location)
		{
			return Location.Exists()? File.ReadAllText(Location.FullName) : null;
		}

		/// <summary>
		/// Attempts to write an owner to a token file transactionally
		/// </summary>
		/// <returns>True if the lock was acquired, false otherwise</returns>
		public bool WriteTokenFile(FileReference Location, string Signature)
		{
			// Check it doesn't already exist
			if(Location.Exists())
			{
				return false;
			}

			// Make sure the directory exists
			Location.Directory.CreateDirectory();

			// Create a temp file containing the owner name
			string TempFileName;
			for(int Idx = 0;;Idx++)
			{
				TempFileName = String.Format("{0}.{1}.tmp", Location.FullName, Idx);
				try
				{
					byte[] Bytes = Encoding.UTF8.GetBytes(Signature);
					using (FileStream Stream = File.Open(TempFileName, FileMode.CreateNew, FileAccess.Write, FileShare.None))
					{
						Stream.Write(Bytes, 0, Bytes.Length);
					}
					break;
				}
				catch(IOException)
				{
					if(!File.Exists(TempFileName))
					{
						throw;
					}
				}
			}

			// Try to move the temporary file into place. 
			try
			{
				File.Move(TempFileName, Location.FullName);
				return true;
			}
			catch
			{
				if(!File.Exists(TempFileName))
				{
					throw;
				}
				return false;
			}
		}

		/// <summary>
		/// Checks whether the given assembly is a publically distributed engine assembly.
		/// </summary>
		/// <param name="File">Assembly location</param>
		/// <returns>True if the assembly is distributed publically</returns>
		static bool IsPublicAssembly(FileReference File)
		{
			DirectoryReference EngineDirectory = UnrealBuildTool.UnrealBuildTool.EngineDirectory;
			if(File.IsUnderDirectory(EngineDirectory))
			{
				string[] PathFragments = File.MakeRelativeTo(EngineDirectory).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
				if(PathFragments.All(x => !x.Equals("NotForLicensees", StringComparison.InvariantCultureIgnoreCase) && !x.Equals("NoRedist", StringComparison.InvariantCultureIgnoreCase)))
				{
					return true;
				}
			}
			return false;
		}

		/// <summary>
		/// Find all the nodes in the graph which are already completed
		/// </summary>
		/// <param name="Graph">The graph instance</param>
		/// <param name="Storage">The temp storage backend which stores the shared state</param>
		HashSet<Node> FindCompletedNodes(Graph Graph, TempStorage Storage)
		{
			HashSet<Node> CompletedNodes = new HashSet<Node>();
			foreach(Node Node in Graph.Agents.SelectMany(x => x.Nodes))
			{
				if(Storage.IsComplete(Node.Name))
				{
					CompletedNodes.Add(Node);
				}
			}
			return CompletedNodes;
		}

		/// <summary>
		/// Builds all the nodes in the graph
		/// </summary>
		/// <param name="Job">Information about the current job</param>
		/// <param name="Graph">The graph instance</param>
		/// <returns>True if everything built successfully</returns>
		bool BuildAllNodes(JobContext Job, Graph Graph, TempStorage Storage)
		{
			// Build a flat list of nodes to execute, in order
			Node[] NodesToExecute = Graph.Agents.SelectMany(x => x.Nodes).ToArray();

			// Check the integrity of any local nodes that have been completed. It's common to run formal builds locally between regular development builds, so we may have 
			// stale local state. Rather than failing later, detect and clean them up now.
			HashSet<Node> CleanedNodes = new HashSet<Node>();
			foreach(Node NodeToExecute in NodesToExecute)
			{
				if(NodeToExecute.InputDependencies.Any(x => CleanedNodes.Contains(x)) || !Storage.CheckLocalIntegrity(NodeToExecute.Name, NodeToExecute.Outputs.Select(x => x.TagName)))
				{
					Storage.CleanLocalNode(NodeToExecute.Name);
					CleanedNodes.Add(NodeToExecute);
				}
			}

			// Execute them in order
			int NodeIdx = 0;
			foreach(Node NodeToExecute in NodesToExecute)
			{
				Log("****** [{0}/{1}] {2}", ++NodeIdx, NodesToExecute.Length, NodeToExecute.Name);
				if(!Storage.IsComplete(NodeToExecute.Name))
				{
					Log("");
					if(!BuildNode(Job, Graph, NodeToExecute, Storage, false))
					{
						return false;
					} 
					Log("");
				}
			}
			return true;
		}

		/// <summary>
		/// Build a node
		/// </summary>
		/// <param name="Job">Information about the current job</param>
		/// <param name="Graph">The graph to which the node belongs. Used to determine which outputs need to be transferred to temp storage.</param>
		/// <param name="Node">The node to build</param>
		/// <returns>True if the node built successfully, false otherwise.</returns>
		bool BuildNode(JobContext Job, Graph Graph, Node Node, TempStorage Storage, bool bWithBanner)
		{
			DirectoryReference RootDir = new DirectoryReference(CommandUtils.CmdEnv.LocalRoot);

			// Create the mapping of tag names to file sets
			Dictionary<string, HashSet<FileReference>> TagNameToFileSet = new Dictionary<string,HashSet<FileReference>>();

			// Read all the input tags for this node, and build a list of referenced input storage blocks
			HashSet<TempStorageBlock> InputStorageBlocks = new HashSet<TempStorageBlock>();
			foreach(NodeOutput Input in Node.Inputs)
			{
				TempStorageFileList FileList = Storage.ReadFileList(Input.ProducingNode.Name, Input.TagName);
				TagNameToFileSet[Input.TagName] = FileList.ToFileSet(RootDir);
				InputStorageBlocks.UnionWith(FileList.Blocks);
			}

			// Read all the input storage blocks, keeping track of which block each file came from
			Dictionary<FileReference, TempStorageBlock> FileToStorageBlock = new Dictionary<FileReference, TempStorageBlock>();
			foreach(TempStorageBlock InputStorageBlock in InputStorageBlocks)
			{
				TempStorageManifest Manifest = Storage.Retreive(InputStorageBlock.NodeName, InputStorageBlock.OutputName);
				foreach(FileReference File in Manifest.Files.Select(x => x.ToFileReference(RootDir)))
				{
					TempStorageBlock CurrentStorageBlock;
					if(FileToStorageBlock.TryGetValue(File, out CurrentStorageBlock))
					{
						LogError("File '{0}' was produced by {1} and {2}", InputStorageBlock, CurrentStorageBlock);
					}
					FileToStorageBlock[File] = InputStorageBlock;
				}
			}

			// Add placeholder outputs for the current node
			foreach(NodeOutput Output in Node.Outputs)
			{
				TagNameToFileSet.Add(Output.TagName, new HashSet<FileReference>());
			}

			// Execute the node
			if(bWithBanner)
			{
				Console.WriteLine();
				CommandUtils.Log("========== Starting: {0} ==========", Node.Name);
			}
			if(!Node.Build(Job, TagNameToFileSet))
			{
				return false;
			}
			if(bWithBanner)
			{
				CommandUtils.Log("========== Finished: {0} ==========", Node.Name);
				Console.WriteLine();
			}

			// Determine all the output files which are required to be copied to temp storage (because they're referenced by nodes in another agent)
			HashSet<FileReference> ReferencedOutputFiles = new HashSet<FileReference>();
			foreach(Agent Agent in Graph.Agents)
			{
				bool bSameAgent = Agent.Nodes.Contains(Node);
				foreach(Node OtherNode in Agent.Nodes)
				{
					if(!bSameAgent || Node.ControllingTrigger != OtherNode.ControllingTrigger)
					{
						foreach(NodeOutput Input in OtherNode.Inputs.Where(x => x.ProducingNode == Node))
						{
							ReferencedOutputFiles.UnionWith(TagNameToFileSet[Input.TagName]);
						}
					}
				}
			}

			// Find a block name for all new outputs
			Dictionary<FileReference, string> FileToOutputName = new Dictionary<FileReference, string>();
			foreach(NodeOutput Output in Node.Outputs)
			{
				HashSet<FileReference> Files = TagNameToFileSet[Output.TagName]; 
				foreach(FileReference File in Files)
				{
					if(!FileToStorageBlock.ContainsKey(File) && File.IsUnderDirectory(RootDir))
					{
						if(Output == Node.DefaultOutput)
						{
							if(!FileToOutputName.ContainsKey(File))
							{
								FileToOutputName[File] = "";
							}
						}
						else
						{
							string OutputName;
							if(FileToOutputName.TryGetValue(File, out OutputName) && OutputName.Length > 0)
							{
								FileToOutputName[File] = String.Format("{0}+{1}", OutputName, Output.TagName.Substring(1));
							}
							else
							{
								FileToOutputName[File] = Output.TagName.Substring(1);
							}
						}
					}
				}
			}

			// Invert the dictionary to make a mapping of storage block to the files each contains
			Dictionary<string, HashSet<FileReference>> OutputStorageBlockToFiles = new Dictionary<string, HashSet<FileReference>>();
			foreach(KeyValuePair<FileReference, string> Pair in FileToOutputName)
			{
				HashSet<FileReference> Files;
				if(!OutputStorageBlockToFiles.TryGetValue(Pair.Value, out Files))
				{
					Files = new HashSet<FileReference>();
					OutputStorageBlockToFiles.Add(Pair.Value, Files);
				}
				Files.Add(Pair.Key);
			}

			// Write all the storage blocks, and update the mapping from file to storage block
			foreach(KeyValuePair<string, HashSet<FileReference>> Pair in OutputStorageBlockToFiles)
			{
				TempStorageBlock OutputBlock = new TempStorageBlock(Node.Name, Pair.Key);
				foreach(FileReference File in Pair.Value)
				{
					FileToStorageBlock.Add(File, OutputBlock);
				}
				Storage.Archive(Node.Name, Pair.Key, Pair.Value.ToArray(), Pair.Value.Any(x => ReferencedOutputFiles.Contains(x)));
			}

			// Publish all the output tags
			foreach(NodeOutput Output in Node.Outputs)
			{
				HashSet<FileReference> Files = TagNameToFileSet[Output.TagName];

				HashSet<TempStorageBlock> StorageBlocks = new HashSet<TempStorageBlock>();
				foreach(FileReference File in Files)
				{
					TempStorageBlock StorageBlock;
					if(FileToStorageBlock.TryGetValue(File, out StorageBlock))
					{
						StorageBlocks.Add(StorageBlock);
					}
				}

				Storage.WriteFileList(Node.Name, Output.TagName, Files, StorageBlocks.ToArray());
			}

			// Mark the node as succeeded
			Storage.MarkAsComplete(Node.Name);
			return true;
		}
	}

	/// <summary>
	/// Legacy command name for compatibility.
	/// </summary>
	public class Build : BuildGraph
	{
	}
}
