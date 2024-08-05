using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

// this front half of this app is mostly agnostic to FB and it's mannerisms
// e.g. all the type, costant, interface, function etc collection 
// just parses out the information into structures
//
// Where the FB specific stuff is in FileOutput.cs, which necessarily output FB specific syntax
// there's also the type decoding in TypeHandleInfo which converts the c# types in the metadata to FB ones
// All the FB specific code is marked with comments mentioning 'FB SPECIFIC' so if you search for those words
// (without the quotes) you should find them all

namespace MetadataParser
{
    static class App
    {
        internal struct OptionSet
        {
            public string winmdFile;
            public string injectionsFile;
            public string fixesFile;
            public string outputDirectory;
            public string[] namespaces;
            public bool onlyListNS;
            public bool niceWrappers;
            public bool generateGuidFiles;
        }

        // This namespace just contains the attributes that apply to everything else
        // it doesn't contain anything we need to translate
        internal const string ATTRIBUTE_NAMESPACE = "Windows.Win32.Foundation.Metadata";

        private static void ParseArguments(string[] args, ref OptionSet opts)
        {
            int numStrings = args.Length;
            for (int i = 0; i < numStrings; ++i)
            {
                string lowerArg = args[i].ToLowerInvariant();
                switch (lowerArg)
                {
                    case "-i":
                    {
                        if(((i + 1) >= numStrings) || !File.Exists(args[i + 1]))
                        {
                            throw new ArgumentException("-i option requires an existing winmd file");
                        }
                        opts.winmdFile = args[i + 1];
                        ++i;
                    }
                    break;
                    case "-j":
                    {
                        if (((i + 1) >= numStrings) || !File.Exists(args[i + 1]))
                        {
                            throw new ArgumentException("-j option requires an existing text file");
                        }
                        opts.injectionsFile = args[i + 1];
                        ++i;
                    }
                    break;
                    case "-ns":
                    {
                        if ((i + 1) >= numStrings)
                        {
                            throw new ArgumentException("-ns option requires a list of namespaces separated by commas");
                        }
                        opts.namespaces = args[i + 1].ToLowerInvariant().Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                    }
                    break;
                    case "-l":
                    {
                        opts.onlyListNS = true;
                    }
                    break;
                    case "-w":
                    {
                        opts.niceWrappers = true;
                    }
                    break;
                    case "-g":
                    {
                        opts.generateGuidFiles = true;
                    }
                    break;
                    case "-f":
                    {
                        if (((i + 1) >= numStrings) || !File.Exists(args[i + 1]))
                        {
                            throw new ArgumentException("-f option requires the valid name of a fixes file");
                        }
                        opts.fixesFile = args[i + 1];
                    }
                    break;
                    case "-o":
                    {
                        if (((i + 1) >= numStrings) || (args[i + 1].IndexOfAny(Path.GetInvalidPathChars()) != -1))
                        {
                            throw new ArgumentException("-o option requires the valid name of an output directory");
                        }
                        opts.outputDirectory = args[i + 1];
                    }
                    break;
                }
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine(
                "FBWindowsHeaderGen{0}" +
                "Usage: FBWindowsHeaderGen -i winmd_file -o output_directory <optional_args>{0}" +
                "Where optional args are:{0}" +
                "-ns namespace,list  - a comma separated list of namespaces to generate headers for{0}" +
                "                      defaults to all if not set{0}" +
                "-l                  - list available namespaces only, no generation{0}" +
                "-w                  - Generate 'nice' wrappers for types and functions{0}" +
                "-g                  - Generate .bas files for each guid to build libuuid.a{0}" +
                "-j                  - specifies a file with injections for missing definitions{0}" +
                "-f                  - path to a 'name fixes' file to move and rename types{0}" +
                "{0}" +
                "-i and -o are required. The directory specified in -o will be created if it doesn't exist{0}" +
                "if -g is specified, the files are placed in output_directory/GUIDFiles dir",
                Environment.NewLine
            );
        }

        static void EnumNamespacesInner(MetadataReader metaReader, NamespaceDefinition parent, Dictionary<string, NamespaceDefinition> nsList)
        {
            foreach (NamespaceDefinitionHandle nd in parent.NamespaceDefinitions)
            {
                NamespaceDefinition ndDef = metaReader.GetNamespaceDefinition(nd);
                // if this namespace has nothing in it, we don't care
                if (ndDef.TypeDefinitions.Length > 0)
                {
                    string nsName = metaReader.GetString(nd);
                    // exclude the one that has nothing in it
                    if (nsName != ATTRIBUTE_NAMESPACE)
                    {
                        nsList.Add(nsName.ToLowerInvariant(), ndDef);
                    }
                    else
                    {
                        CustomAttributeParser.Init(metaReader, ndDef);
                    }
                }
                EnumNamespacesInner(metaReader, ndDef, nsList);
            }
        }

        static Dictionary<string, NamespaceDefinition> EnumNamespaces(MetadataReader metaReader, NamespaceDefinition root)
        {
            Dictionary<string, NamespaceDefinition> nsList = new Dictionary<string, NamespaceDefinition>(100);
            EnumNamespacesInner(metaReader, root, nsList);
            return nsList;
        }

        static TypeCollector CreateTypeProcessor(MetadataReader mr, Dictionary<string, NamespaceDefinition> nsList, OptionSet opts, NameChangeDB nameFixes)
        {
            Dictionary<string, NamespaceDefinition> selected = null;
            Dictionary<string, HashSet<string>> dependentNamespaces = nameFixes.GetNamespaceDependencies();
            if(opts.namespaces != null)
            {
                selected = new Dictionary<string, NamespaceDefinition>();
                foreach (string choice in opts.namespaces)
                {
                    NamespaceDefinition nsDef;
                    if(nsList.TryGetValue(choice, out nsDef))
                    {
                        selected.Add(choice, nsDef);
                        HashSet<string> dependents;
                        if(dependentNamespaces.TryGetValue(choice, out dependents))
                        {
                            foreach (string depNs in dependents)
                            {
                                NamespaceDefinition depDef;
                                if(nsList.TryGetValue(depNs, out depDef))
                                {
                                    selected.TryAdd(depNs, depDef);
                                }
                            }
                        }
                    }
                }
            }
            return new TypeCollector(mr, nsList, selected, nameFixes);
        }

        static void PrintContents(TypeCollectionResults results)
        {
            Console.WriteLine("Found {0} namespaces", results.Contents.Count);
            foreach (NamespaceContent cont in results.Contents.Values)
            {
                Console.WriteLine("Namespace: {0}", cont.Name);
                string indent = "    ";
                string secondLevelIndent = indent + indent;
                //foreach(KeyValuePair<Version, RawTypeEntries> osentries in cont.MinimumOsEntries)
                {
                    //RawTypeEntries types = osentries.Value;
                    RawTypeEntries types = cont.TypeEntries;
                    if (types.Constants.Count > 0)
                    {
                        Console.WriteLine("{0}Constants:", indent);
                        foreach (ConstantValue cv in types.Constants)
                        {
                            Console.WriteLine("{0}{1}", secondLevelIndent, cv.varType.Name);
                        }
                    }
                    if (types.Enums.Count > 0)
                    {
                        Console.WriteLine("{0}Enums:", indent);
                        foreach (StructType<ConstantValue> e in types.Enums)
                        {
                            Console.WriteLine("{0}{1}", secondLevelIndent, e.Name);
                        }
                    }
                    if (types.Functions.Count > 0)
                    {
                        Console.WriteLine("{0}Functions:", indent);
                        foreach (FunctionType fun in types.Functions)
                        {
                            Console.WriteLine("{0}{1}", secondLevelIndent, fun.Name);
                        }
                    }
                    if (types.FunctionPointers.Count > 0)
                    {
                        Console.WriteLine("{0}Function Pointers:", indent);
                        foreach (FunctionPointerType ptr in types.FunctionPointers)
                        {
                            Console.WriteLine("{0}{1}", secondLevelIndent, ptr.Name);
                        }
                    }
                    if (types.Interfaces.Count > 0)
                    {
                        Console.WriteLine("{0}Interfaces:", indent);
                        foreach (StructType<FunctionType> iface in types.Interfaces)
                        {
                            Console.WriteLine("{0}{1}", secondLevelIndent, iface.Name);
                        }
                    }
                    if (types.Structs.Count > 0)
                    {
                        Console.WriteLine("{0}Structs:", indent);
                        foreach (StructType<VarType> stru in types.Structs)
                        {
                            Console.WriteLine("{0}{1}", secondLevelIndent, stru.Name);
                        }
                    }
                    Console.WriteLine();
                }
                Console.WriteLine();
            }
        }

        static private Dictionary<string, Dictionary<string, string>> LoadInjections(string formattedFile)
        {
            Dictionary<string, Dictionary<string, string>> injections = new Dictionary<string, Dictionary<string, string>>();
            if(formattedFile == String.Empty)
            {
                return injections;
            }
            string[] lines = File.ReadAllLines(formattedFile);
            string curNamespace = String.Empty;
            string curItem = String.Empty;
            StringBuilder currentText = new StringBuilder();
            Dictionary<string, string> nsInjections = new Dictionary<string, string>();
            foreach (string s in lines)
            {
                if (s.Length > 0)
                {
                    if (s[0] == '#') continue;
                    if (s[0] == '-')
                    {
                        if (s.Length > 2)
                        {
                            if (currentText.Length > 0)
                            {
                                nsInjections.Add(curItem, currentText.ToString());
                                currentText.Length = 0;
                            }
                            // new item type
                            if (s[1] == '-')
                            {
                                string type = s.Substring(2).Trim();
                                curItem = type;
                                currentText.Length = 0;
                            }
                            // new namespace
                            else
                            {
                                string ns = s.Substring(1).Trim().ToLowerInvariant();
                                curNamespace = ns;
                                curItem = String.Empty;
                                nsInjections = new Dictionary<string, string>();
                                injections.Add(curNamespace, nsInjections);
                            }
                        }
                    }
                    else
                    {
                        currentText.AppendLine(s);
                    }
                }
                else if(curItem != String.Empty)
                {
                    currentText.AppendLine();
                }
            }
            nsInjections.Add(curItem, currentText.ToString());
            return injections;
        }

        public static void Main(string[] args)
        {
            if(args.Length == 0)
            {
                PrintUsage();
                return;
            }
            OptionSet opts = new OptionSet();
            try
            {
                ParseArguments(args, ref opts);
                TypeCollectionResults collected;
                NameChangeDB nameFixes = NameChangeDB.Load(opts.fixesFile);
                using (FileStream fs = File.OpenRead(opts.winmdFile))
                using (PEReader peReader = new PEReader(fs))
                {
                    MetadataReader metaReader = peReader.GetMetadataReader();
                    NamespaceDefinition rootNs = metaReader.GetNamespaceDefinitionRoot();
                    Dictionary<string, NamespaceDefinition> ns = EnumNamespaces(metaReader, rootNs);
                    TypeCollector proc = CreateTypeProcessor(metaReader, ns, opts, nameFixes);
                    collected = proc.DoWork();
                }
                if (!opts.onlyListNS)
                {
                    OutputCreator oc = new OutputCreator(collected);
                    oc.WriteNamespaceHeaders(opts, LoadInjections(opts.injectionsFile));
                }
                else
                {
                    PrintContents(collected);
                }
            }
            catch(Exception e)
            {
                Debug.WriteLine(String.Format("Caught exception {0} of type {1}", e.Message, e.GetType().FullName));
                Console.WriteLine(e.Message);
            }
        }
    }
}
