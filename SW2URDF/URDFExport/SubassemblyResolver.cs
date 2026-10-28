/*
Copyright (c) 2015 Stephen Brawner

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.  IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
*/

using log4net;
using SolidWorks.Interop.sldworks;
using SW2URDF.URDF;
using SW2URDF.Utilities;
using System.Collections.Generic;
using System.Linq;

namespace SW2URDF.URDFExport
{
    /// <summary>
    /// Expands the components selected for each link down to the part (leaf) components
    /// that actually carry geometry, recursing through any depth of sub-assemblies.
    ///
    /// Ownership rule: a part belongs to the link that selected its nearest ancestor
    /// (or the part itself). So a link may select a whole sub-assembly while a child link
    /// selects a deeper sub-assembly or individual parts inside it; those parts are then
    /// removed from the parent link automatically.
    /// </summary>
    public static class SubassemblyResolver
    {
        private static readonly ILog logger = Logger.GetLogger();

        /// <summary>
        /// Fills Link.SWLeafComponents for every link of the tree.
        /// </summary>
        /// <returns>An error message, or an empty string on success</returns>
        public static string ResolveLeafComponents(ModelDoc2 model, LinkNode baseNode)
        {
            AssemblyDoc assy = (AssemblyDoc)model;
            // Lightweight components don't expose their bodies or children reliably
            assy.ResolveAllLightWeightComponents(false);

            List<Link> links = new List<Link>();
            CollectLinks(baseNode, links);

            // Map of selected component name -> link which selected it
            Dictionary<string, Link> owners = new Dictionary<string, Link>();
            List<string> conflicts = new List<string>();
            foreach (Link link in links)
            {
                foreach (Component2 comp in link.SWComponents)
                {
                    if (owners.TryGetValue(comp.Name2, out Link other) && other != link)
                    {
                        conflicts.Add(comp.Name2 + " (" + other.Name + ", " + link.Name + ")");
                    }
                    else
                    {
                        owners[comp.Name2] = link;
                    }
                }
            }

            if (conflicts.Count > 0)
            {
                return "The following components were selected by more than one link:\r\n" +
                    string.Join("\r\n", conflicts);
            }

            foreach (Link link in links)
            {
                Dictionary<string, Component2> leaves = new Dictionary<string, Component2>();
                foreach (Component2 comp in link.SWComponents)
                {
                    foreach (Component2 leaf in GetLeafComponents(comp))
                    {
                        if (FindOwner(leaf, owners) == link)
                        {
                            leaves[leaf.Name2] = leaf;
                        }
                    }
                }
                link.SWLeafComponents = leaves.Values.ToList();
                logger.Info("Link " + link.Name + ": " + link.SWComponents.Count +
                    " selected components expanded to " + link.SWLeafComponents.Count + " parts");
            }

            LogUnassignedParts(assy, owners);
            return "";
        }

        /// <summary>
        /// Returns the part components below (and including) a component, skipping suppressed
        /// components and envelopes.
        /// </summary>
        public static List<Component2> GetLeafComponents(Component2 comp)
        {
            List<Component2> leaves = new List<Component2>();
            if (comp == null || comp.IsSuppressed() || comp.IsEnvelope())
            {
                return leaves;
            }

            object[] children = (object[])comp.GetChildren();
            if (children == null || children.Length == 0)
            {
                leaves.Add(comp);
                return leaves;
            }

            foreach (Component2 child in children)
            {
                leaves.AddRange(GetLeafComponents(child));
            }
            return leaves;
        }

        /// <summary>
        /// Returns all part components of an assembly at any depth
        /// </summary>
        public static List<Component2> GetAllLeafComponents(AssemblyDoc assy)
        {
            List<Component2> leaves = new List<Component2>();
            object[] topLevel = (object[])assy.GetComponents(true);
            if (topLevel != null)
            {
                foreach (Component2 comp in topLevel)
                {
                    leaves.AddRange(GetLeafComponents(comp));
                }
            }
            return leaves;
        }

        private static Link FindOwner(Component2 leaf, Dictionary<string, Link> owners)
        {
            Component2 current = leaf;
            while (current != null)
            {
                if (owners.TryGetValue(current.Name2, out Link owner))
                {
                    return owner;
                }
                current = current.GetParent();
            }
            return null;
        }

        private static void CollectLinks(LinkNode node, List<Link> links)
        {
            links.Add(node.Link);
            foreach (LinkNode child in node.Nodes)
            {
                CollectLinks(child, links);
            }
        }

        private static void LogUnassignedParts(AssemblyDoc assy, Dictionary<string, Link> owners)
        {
            List<string> unassigned = GetAllLeafComponents(assy)
                .Where(leaf => !leaf.IsHidden(true) && FindOwner(leaf, owners) == null)
                .Select(leaf => leaf.Name2)
                .ToList();
            if (unassigned.Count > 0)
            {
                logger.Warn(unassigned.Count + " visible parts are not assigned to any link: " +
                    string.Join(", ", unassigned));
            }
        }
    }
}
