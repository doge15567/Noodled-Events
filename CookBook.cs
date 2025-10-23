#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Drawing.Printing;
using System.Linq;
using System.Reflection;
using TMPro;
using UltEvents;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.UIElements;

namespace NoodledEvents
{
    /// <summary>
    /// A Node refers to a CookBook for compilation;
    /// A CookBook provides a List of addable nodes, while handling the construction of these nodes (serialized side only)
    /// </summary>
    public class CookBook : ScriptableObject
    {
        private static Assembly _blAssmb;
        private static Assembly _xrAssmb;
        public static Assembly BLAssembly => _blAssmb ??= AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(ass => ass.FullName.StartsWith("Assembly-CSharp"));
        public static Assembly XRAssembly => _xrAssmb ??= AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(ass => ass.FullName.StartsWith("Unity.XR.Interaction.Toolkit"));
        public static Type GetExtType(string name, Assembly ass = null) 
        {
            ass ??= BLAssembly;
            if (ass == null)
                return null;
            foreach (Type t in ass.GetTypes())
                try
                {
                    if (t.Name == name) return t;
                }
                catch (TypeLoadException) { }
            return null;
        }
        // decided these were so usefull, that they should be accessible accross books.
        protected static MethodInfo SetActive = typeof(GameObject).GetMethod("SetActive");
        protected static PropertyInfo GetSetLocPos = typeof(Transform).GetProperty("localPosition");
        protected static MethodInfo Translate = typeof(Transform).GetMethod("Translate", new Type[] { typeof(float), typeof(float), typeof(float) });
        public virtual void CollectDefs(Action<IEnumerable<NodeDef>, float> progressCallback, Action completedCallback) 
        {
            completedCallback.Invoke();
        }

        private static SerializedNode lastCompiledNode;
        public virtual void CompileNode(UltEventBase evt, SerializedNode node, Transform dataRoot)
        {
            // when a bowl is compiled, it puts forward an evt that is filled by compiled nodes.
            // this func handles it
            // method overrides of this func should also call base.CompileNode(evt, node, dataRoot);
            // so that errors can be displayed on the node
            node.Bowl.ErroredNode = node;

            // if any of our inputs are connected to a redirect, we need to find the real source and copy its compEvt/compCall
            foreach (var input in node.DataInputs.Where(di => di.Source?.Node?.NoadType == SerializedNode.NodeType.Redirect))
            {
                var redirectChain = new List<SerializedNode>();
                var current = input.Source.Node;

                // collect the redirect chain
                while (current != null && current.NoadType == SerializedNode.NodeType.Redirect)
                {
                    redirectChain.Add(current);
                    current = current.DataInputs[0].Source?.Node;
                }

                // current is now the source node (or null if not connected)

                if (current != null && current.DataOutputs.Length > 0)
                {
                    var compEvt = current.DataOutputs[0].CompEvt;
                    var call = current.DataOutputs[0].CompCall;

                    // set all redirects in the chain to use this compEvt/compCall
                    for (int i = redirectChain.Count - 1; i >= 0; i--)
                    {
                        var r = redirectChain[i];
                        r.DataOutputs[0].CompEvt = compEvt;
                        r.DataOutputs[0].CompCall = call;
                    }
                }
            }
        }
        public virtual void PostCompile(SerializedBowl bowl)
        {
            // ran when a bowl with a nodedef from this book has been compiled (and GetType injected!)
            // lalala
        }
        // Ran one-shot when user hovers over the "Alternatives" dropdown bt
        // string is the button, NodeDef is the node
        public virtual Dictionary<string, NodeDef> GetAlternatives(SerializedNode node)
        {
            // Provides possible replacements for a node
            return null;
        }
        // ran when one node becomes another
        public virtual void SwapConnections(SerializedNode oldNode, SerializedNode newNode)
        {
            if (oldNode.FlowInputs.Length > 0 && newNode.FlowInputs.Length > 0)
                foreach (var fsrc in oldNode.FlowInputs[0].Sources.ToList())
                    fsrc.Connect(newNode.FlowInputs[0]);

            if (oldNode.FlowOutputs.Length > 0 && newNode.FlowOutputs.Length > 0)
                if (oldNode.FlowOutputs[0].Target != null)
                    newNode.FlowOutputs[0].Connect(oldNode.FlowOutputs[0].Target);
        }

        public static PersistentCall MakeCall(string method)
        {
            var c = new PersistentCall();
            c.FSetMethodName(method);
            return c;
        }
        public static PersistentCall MakeCall<T>(string method, params Type[] ts)
            => new PersistentCall(typeof(T).GetMethod(method, UltEventUtils.AnyAccessBindings, null, ts, null), null);
        public static PersistentCall MakeCall(Type t, string method, params Type[] ts)
            => new PersistentCall(t.GetMethod(method, UltEventUtils.AnyAccessBindings, null, ts, null), null);
        public static PersistentCall MakeCall<T>(string method, UnityEngine.Object obj = null, params Type[] ts)
            => new PersistentCall(typeof(T).GetMethod(method, UltEventUtils.AnyAccessBindings, null, ts, null), obj);
        public static PersistentCall MakeCall<T>(string method, UnityEngine.Object obj = null)
            => new PersistentCall(typeof(T).GetMethod(method, UltEventUtils.AnyAccessBindings), obj);

        public class PendingConnection // utility class to link pcalls, with support for cross-event data transfer
        { 
            /// <summary>
            /// Super generic NoodleOut -> PersistentCallArgIn
            /// </summary>
            /// <param name="o"></param>
            /// <param name="targEvt"></param>
            /// <param name="targCall"></param>
            /// <param name="argIdx"></param>
            public PendingConnection(NoodleDataOutput o, UltEventBase targEvt, PersistentCall targCall, int argIdx) 
            {
                TargEvent = targEvt; TargCall = targCall;
                TargInwardType = targCall.Method.GetParameters()[argIdx].ParameterType; TargInput = argIdx;
                if (o.Node.NoadType == SerializedNode.NodeType.Redirect) // Handle redirect nodes /// From CookBook.CompileNode
                {
                    SerializedNode secondtolast = null;
                    var current = o.Node;

                    // ride the redirect chain
                    while (current != null && current.NoadType == SerializedNode.NodeType.Redirect)
                    {
                        secondtolast = current;
                        current = current.DataInputs[0].Source?.Node;
                    }
                    // current is now the source node (or null if not connected)
                    // find out what connection was used
                    o = secondtolast.DataInputs[0].Source;

                    if (o == null) // Redirect with missing wire on left side
                    {
                        return;
                    }
                }
                SourceEvent = o.CompEvt; SourceCall = o.CompCall;
                SourceOutwardType = o.Type.Type;
                if (o.Node.NoadType == SerializedNode.NodeType.BowlInOut)
                    ArgIsSource = Array.IndexOf(o.Node.DataOutputs, o);
                else if (o.UseCompAsParam)
                    ArgIsSource = o.CompAsParam;
                else ArgIsSource = -1;
            }

            // Comps used to transfer data between events
            public static Dictionary<Type, (Type, PropertyInfo)> CompStoragers = new Dictionary<Type, (Type, PropertyInfo)>()
            {
                { typeof(UnityEngine.Object), (GetExtType("XRInteractorAffordanceStateProvider", XRAssembly), GetExtType("XRInteractorAffordanceStateProvider", XRAssembly).GetProperty("interactorSource", UltEventUtils.AnyAccessBindings)) },
                { typeof(float), (typeof(UnityEngine.UI.AspectRatioFitter), RatioGetSet) },
                { typeof(Material[]), (typeof(MeshRenderer), typeof(MeshRenderer).GetProperty("sharedMaterials", UltEventUtils.AnyAccessBindings)) },
                { typeof(bool), (typeof(UnityEngine.UI.Mask), typeof(UnityEngine.UI.Mask).GetProperty("enabled")) },
                { typeof(Vector3), (typeof(PositionConstraint), typeof(PositionConstraint).GetProperty(nameof(PositionConstraint.translationOffset))) },
                { typeof(string), (typeof(TextMeshPro), typeof(TMP_Text).GetProperty("text", UltEventUtils.AnyAccessBindings)) },
                { typeof(int), (typeof(LineRenderer), typeof(LineRenderer).GetProperty("numCapVertices", UltEventUtils.AnyAccessBindings)) },
                { typeof(Vector2), (typeof(RectTransform), typeof(RectTransform).GetProperty("sizeDelta", UltEventUtils.AnyAccessBindings)) }
            };
            /* Todo types for CompStoragers
            {typeof(Vector3), "Vector3"},
            {typeof(uint), "uint"},
            {typeof(long), "long"},
            { typeof(ulong), "ulong"},
            { typeof(short), "short"},
            { typeof(ushort), "ushort"},
            { typeof(byte), "byte"},
            { typeof(sbyte), "sbyte"},
            { typeof(double), "double"},
            { typeof(decimal), "decimal"},
            { typeof(char), "char"},
            // these remains are pretty uncommon, i'll implement them later
            */

            public int ArgIsSource; // if this is from an arg (-1 means no >= 0 gives arg idx)
            public UltEventBase SourceEvent;
            public PersistentCall SourceCall;

            public UltEventBase TargEvent;
            public PersistentCall TargCall;
            public Type TargInwardType;
            public Type SourceOutwardType;
            public int TargInput; // the idx of the arg on the TargCall to set as Arg
            public void Connect(Transform dataRoot) // fyi this is called while the targcall is being constructed
            {
                if (SourceEvent == null) 
                {
                    // People dont typically have warnings on in the log
                    Debug.LogWarning("A data redirect node is connected to a node on the right but not the left!\n" +
                        $"Method of node is {TargCall.MethodName}");
                    // So il make the recieved input a proper null instead of leaving None or ret -1
                    /// I think that new nodes with object parameters should be initialized with this instead of none, as it's more intuitive
                    TargCall.PersistentArguments[TargInput] = new PersistentArgument().ToObjVal(null, TargInwardType);
                    return;
                    // TODO: make redirect node show as red
                }

                if (SourceEvent == TargEvent) // same evt connection
                {
                    if (ArgIsSource > -1)
                        TargCall.PersistentArguments[TargInput] = new PersistentArgument().ToParamVal(ArgIsSource, TargInwardType);
                    else
                        TargCall.PersistentArguments[TargInput] = new PersistentArgument().ToRetVal(SourceEvent.PersistentCallsList.IndexOf(SourceCall), TargInwardType);
                }
                else
                {
                    // Source evt != targ event.
                    // to transfer data, we need a temp component to store data in

                    // for UnityEngine.Object, this is easy
                    // all the other types (int, float, color, bool) are todo.

                        
                    Type transferredType = TargInwardType;
                    if (transferredType.IsAssignableFrom(SourceOutwardType))
                        transferredType = SourceOutwardType;

                    foreach (var kvp in CompStoragers)
                    {
                        if (!(transferredType == kvp.Key || transferredType.IsSubclassOf(kvp.Key)))
                            continue;

                        Type dataT = kvp.Key;
                        Type storageT = kvp.Value.Item1 ?? kvp.Value.Item2.DeclaringType;

                        var compVar = dataRoot.StoreComp(storageT);

                        // set compVar in Source Event
                        PersistentCall varSet = null;
                        if (ArgIsSource == -1)
                        {
                            int sourceIdx = SourceEvent.PersistentCallsList.IndexOf(SourceCall); // source PCall idx
                            varSet = new PersistentCall(kvp.Value.Item2.SetMethod, compVar); // compVar setter PCall
                            varSet.FSetArguments(new PersistentArgument().ToRetVal(sourceIdx, transferredType)); // arg for compVar setter PCall
                            SourceEvent.PersistentCallsList.SafeInsert(sourceIdx + 1, varSet); // add compVar setter PCall directly after source PCall
                        }
                        else
                        {
                            varSet = new PersistentCall(kvp.Value.Item2.SetMethod, compVar); // compVar setter PCall
                            varSet.FSetArguments(new PersistentArgument().ToParamVal(ArgIsSource, transferredType)); // arg for compVar setter PCall
                            SourceEvent.PersistentCallsList.SafeInsert(0, varSet); // add compVar setter PCall directly after source PCall
                        }

                        // make getter pcall for targ evt
                        var getPCall = new PersistentCall(kvp.Value.Item2.GetMethod, compVar);

                        // add the getter pcall
                        TargEvent.PersistentCallsList.Add(getPCall);

                        // make targcall ref the gotten value (remember, targcall is under construction rn so its gonna be added last)
                        TargCall.PersistentArguments[TargInput] = new PersistentArgument().ToRetVal(TargEvent.PersistentCallsList.Count - 1, TargInwardType);

                        return;
                    }

                    // fail
                    Debug.Log("failed data transfer for " + TargInwardType);
                    
                }
            }
            private static PropertyInfo RatioGetSet => typeof(UnityEngine.UI.AspectRatioFitter)
                .GetProperty(nameof(UnityEngine.UI.AspectRatioFitter.aspectRatio), UltEventUtils.AnyAccessBindings);
        }
        public class NodeDef
        {
            public NodeDef(CookBook book, string name, Func<Pin[]> inputs, Func<Pin[]> outputs, Func<NodeDef, Button> searchItem) 
            {
                CookBook = book; Name = name; Inputs = inputs?.Invoke() ?? new Pin[0]; Outputs = outputs?.Invoke() ?? new Pin[0];
                createSearchItem = searchItem;
            }
            public NodeDef(CookBook book, string name, Func<Pin[]> inputs, Func<Pin[]> outputs, string bookTag = "", string searchTextOverride = "", string tooltipOverride = "") : this(book, name, inputs, outputs, (def) => 
                {
                    var o = new UnityEngine.UIElements.Button(() =>
                    {
                        if (UltNoodleEditor.Editor == null) return;
                        UltNoodleBowl bowl = UltNoodleEditor.Editor.CurrentBowl;
                        if (bowl == null) return;
                        var nod = bowl.AddNode(def.Name, book).MatchDef(def);

                        nod.BookTag = def.BookTag != string.Empty ? def.BookTag : def.Name;

                        nod.Position = UltNoodleEditor.Editor.TreeView.NewNodeSpawnPos;
                        bowl.Validate();
                        UltNoodleEditor.Editor.TreeView.RenderNewNodes();

                        UltNoodleSearchWindow.ForceClose();
                    });
                    o.text = searchTextOverride == string.Empty ? def.Name : searchTextOverride;
                    o.tooltip = tooltipOverride == string.Empty ? o.text : tooltipOverride;
                    return o;
                }){ BookTag = bookTag; }

            public string Name;
            public CookBook CookBook;
            public Pin[] Inputs;
            public Pin[] Outputs;
            public string BookTag;
            public Func<SerializedNode> CreateNode;
            private Func<NodeDef, Button> createSearchItem;
            public Button SearchItem
            {
                get
                {
                    if (_searchItem == null) 
                    {
                        _searchItem = createSearchItem.Invoke(this);
                        _searchItem.style.unityTextAlign = TextAnchor.MiddleLeft;
                    } return _searchItem;
                }
            }
            private Button _searchItem;

            public class Pin
            {
                public Pin(string name) { Name = name; }
                public Pin(string name, Type type, bool @const = false)
                {
                    Name = name; Type = type; Const = @const;
                }
                public string Name;
                public Type Type;
                public bool Const;
                public bool Flow => Type == null;
            }
        }
    }
}
#endif
