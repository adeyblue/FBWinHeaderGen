using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Text;

namespace MetadataParser
{
    class NameChangeDB
    {
        const string ALL_ENTRIES_MARKER = "_";
        public struct AttributeChangeParams
        {
            public object NewValue { get; init; }
            public FieldInfo AffectedField { get; init; }

            public AttributeChangeParams(FieldInfo field, string value)
            {
                if(value == "null")
                {
                    NewValue = null;
                }
                else
                {
                    NewValue = value;
                }
                AffectedField = field;
            }
        }

        public struct Change
        {
            public string Name { get; init; }
            public string Namespace { get; init; }
            private List<AttributeChangeParams> attributeModifiers;

            public Change(string ns, string nom, List<AttributeChangeParams> attrChangers)
            {
                Namespace = ns;
                Name = nom;
                attributeModifiers = attrChangers;
            }

            public void ApplyChanges(CustomAttributeValues attrValues)
            {
                if (attributeModifiers == null) return;
                foreach(AttributeChangeParams change in attributeModifiers)
                {
                    change.AffectedField.SetValue(attrValues, change.NewValue);
                }
            }
        }

        struct NamespaceUpdates
        {
            private Dictionary<string, Change> namesToNewHomes;
            private string everything;
            public NamespaceUpdates(Dictionary<string, Change> names)
            {
                namesToNewHomes = names;
                Change e;
                if(names.TryGetValue(ALL_ENTRIES_MARKER, out e))
                {
                    everything = e.Namespace;
                }
                else
                {
                    everything = null;
                }
            }

            private string TrimNameToLastPortion(string name)
            {
                int lastDot = name.LastIndexOf('.');
                if (lastDot != -1)
                {
                    name = name.Substring(lastDot + 1);
                }
                return name;
            }

            public Change? LookupName(string name)
            {
                if (everything != null)
                {
                    return new Change(everything, TrimNameToLastPortion(name), null);
                }
                Change newNS;
                return namesToNewHomes.TryGetValue(name, out newNS) ? newNS : null;
            }
        }

        private Dictionary<string, NamespaceUpdates> fixes;
        private Dictionary<string, HashSet<string>> dependentNamespaces;

        private bool ParseAttributeAssignment(
            ref List<AttributeChangeParams> attrChanges, 
            string assignment, 
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] Type customAttrValsType
        )
        {
            string[] assParts = assignment.Split('=');
            if (assParts.Length != 2)
            {
                return false;
            }
            FieldInfo changeField = customAttrValsType.GetField(assParts[0].Trim());
            if(changeField != null)
            {
                if (attrChanges == null) attrChanges = new List<AttributeChangeParams>();
                attrChanges.Add(new AttributeChangeParams(changeField, assParts[1].Trim()));
            }
            return changeField != null;
        }

        private Change ParseEntry(
            string curNamespace, 
            string curType, 
            string changes,
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicFields)] Type customAttrValsType
        )
        {
            // parse out the right side of -> entry in the fixes files
            string[] newParts = changes.Split(',');
            int changeSections = newParts.Length;
            string newNS;
            string newName;
            List<AttributeChangeParams> attrChanges = null;
            if (changeSections > 1)
            {
                // namespace
                if (newParts[0].Length > 0)
                {
                    newNS = newParts[0].TrimStart();
                }
                else
                {
                    newNS = curNamespace;
                }
                // new name
                if(newParts[1].Length > 0)
                {
                    newName = newParts[1].Trim();
                }
                else
                {
                    newName = curType;
                }
                // attribute changes
                for(int i = 2; i < changeSections; ++i)
                {
                    if(!ParseAttributeAssignment(ref attrChanges, newParts[i], customAttrValsType))
                    {
                        Trace.WriteLine("Attribute assignment {0} for type {1}.{2} didn't have 2 parts or had invalid field", newParts[i], curNamespace, curType);
                    }
                }
            }
            else
            {
                newNS = changes.Trim();
                newName = curType;
            }
            newNS = newNS.ToLowerInvariant();
            Change nc = new Change(
                newNS,
                newName,
                attrChanges
            );
            HashSet<string> newNSDependents;
            if (!dependentNamespaces.TryGetValue(newNS, out newNSDependents))
            {
                newNSDependents = new HashSet<string>();
                dependentNamespaces.Add(newNS, newNSDependents);
            }
            newNSDependents.Add(curNamespace);
            return nc;
        }

        public NameChangeDB(Dictionary<string, Dictionary<string, string>> changes)
        {
            Type customAttrValsType = typeof(CustomAttributeValues);
            fixes = new Dictionary<string, NamespaceUpdates>(changes.Count);
            dependentNamespaces = new Dictionary<string, HashSet<string>>(changes.Count);
            foreach(KeyValuePair<string, Dictionary<string, string>> nsChanges in changes)
            {
                Dictionary<string, Change> newNames = new Dictionary<string, Change>(nsChanges.Value.Count);
                foreach(KeyValuePair<string, string> namePairs in nsChanges.Value)
                {
                    Change nc = ParseEntry(nsChanges.Key, namePairs.Key, namePairs.Value, customAttrValsType);
                    newNames.Add(namePairs.Key, nc);
                }
                fixes.Add(nsChanges.Key, new NamespaceUpdates(newNames));
            }
        }

        public Change? FindNewAddress(string oldNamespace, string name)
        {
            NamespaceUpdates nsNames;
            return fixes.TryGetValue(oldNamespace, out nsNames) ? nsNames.LookupName(name) : null;
        }

        // this returns a map of destination namespaces to a list of source namespaces that have items that move to it
        // so for instance
        // - windows.win32.system.variant
        // _ -> windows.win32.system.com
        // would return a map of
        // windows.win32.system.com -> windows.win32.system.variant
        // since if windows.win32.system.com is s elected as a namespace
        // windows.win32.system.variant also needs to be parsed to make it complete
        public Dictionary<string, HashSet<string>> GetNamespaceDependencies()
        {
            return dependentNamespaces;
        }

        private static Dictionary<string, Dictionary<string, string>> LoadNameChanges(string file)
        {
            Dictionary<string, Dictionary<string, string>> allFixes = new Dictionary<string, Dictionary<string, string>>();
            string[] lines = File.ReadAllLines(file);
            Dictionary<string, string> nsFixes = new Dictionary<string, string>();
            foreach (string s in lines)
            {
                if (s.Length > 0)
                {
                    if (s[0] == '#') continue;
                    if (s[0] == '-')
                    {
                        if (s.Length > 2)
                        {
                            // new namespace
                            string ns = s.Substring(1).Trim().ToLowerInvariant();
                            nsFixes = new Dictionary<string, string>();
                            if(!allFixes.TryAdd(ns, nsFixes))
                            {
                                nsFixes = allFixes[ns];
                            }
                        }
                    }
                    else
                    {
                        string[] parts = s.Split("->");
                        if (parts.Length != 2)
                        {
                            Debug.WriteLine(String.Format("Fixes line '{0}' has no separator (->), ignoring", s));
                        }
                        else
                        {
                            string nsItem = parts[0].Trim();
                            // don't lowercase this here as it might contain a new type name too
                            string newNamespace = parts[1].Trim();
                            nsFixes.Add(nsItem, newNamespace);
                        }
                    }
                }
            }
            return allFixes;
        }

        public static NameChangeDB Load(string file)
        {
            Dictionary<string, Dictionary<string, string>> formattedDetails;
            if (!String.IsNullOrEmpty(file))
            {
                formattedDetails = LoadNameChanges(file);
            }
            else
            {
                formattedDetails = new Dictionary<string, Dictionary<string, string>>();
            }
            return new NameChangeDB(formattedDetails);
        }
    }
}
