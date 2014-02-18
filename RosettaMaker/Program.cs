using System;
using System.IO;
using IKVM.Reflection;
using IKVM.Reflection.Emit;
using System.Linq;
using iType = IKVM.Reflection.Type;
using System.Collections.Generic;
using Mono.Reflection;

namespace RosettaMaker
{
	class TypePrinter {
		static string LastNS;
		iType t;
		bool captionShown;

		public TypePrinter (iType t)
		{
			this.t = t;
		}

		public void print (string format, params object [] args)
		{
			if (!captionShown) {
				if (LastNS != t.Namespace) {
					Console.WriteLine ("<h1>Namespace: {0}</h1>", t.Namespace);
					LastNS = t.Namespace;
				}

				Console.WriteLine ("<h2>Type: {0}</h2>", t.Name);
				Console.WriteLine ("<div class='member'><a href='http://iosapi.xamarin.com/monodoc.ashx?link=T%3a{0}'>{0}</a> {{", t.FullName);
				captionShown = true;
			}

			var x = String.Format (format, args);
			Console.WriteLine (x);
		}

		public void Close ()
		{
			if (captionShown)
				Console.WriteLine ("</div>");
		}

	}

	class Scanner {
		Universe universe;
		Assembly xamarin_assembly, mscorlib;
		iType nsobject_type, dllimport_type;
		string directory;

		public Scanner (string directory, string library)
		{
			this.directory = directory;

			universe = new Universe (UniverseOptions.DisableFusion);
			universe.AssemblyResolve += (object sender, IKVM.Reflection.ResolveEventArgs args) => {
				Console.WriteLine ("Got a resolve event {0}", args.Name);
				var aname = new AssemblyName (args.Name);

				return universe.LoadFile (directory + "/" + aname.Name + ".dll");
			};
			xamarin_assembly = universe.LoadFile (directory + "/" + library);
			mscorlib = universe.LoadFile (directory + "/mscorlib.dll");

			nsobject_type = xamarin_assembly.GetType ("MonoTouch.Foundation.NSObject");
			dllimport_type = mscorlib.GetType ("System.Runtime.InteropServices.DllImportAttribute");
		}

		public string MakeLink (MethodInfo m)
		{
			string prefix;
			string name;

			if (m.IsSpecialName && m.Name.StartsWith ("get_") || m.Name.StartsWith ("set_")) {
				prefix = "P%3a";
				name = m.Name.Substring (4);
				if (name.StartsWith ("_"))
					name = name.Substring (1);
			} else {
				prefix = "M%3a";
				name = m.Name;
			}

			return "<a href='http://iosapi.xamarin.com/monodoc.ashx?link=" + prefix + 
				m.DeclaringType.FullName + "." + name + "'>" + name + "</a>";
		}

		public string MakeReturn (MethodInfo m)
		{
			var dAss = m.ReturnType.DeclaringType == null ? xamarin_assembly : m.ReturnType.DeclaringType.Assembly;

		
			var t = m.ReturnType.IsArray ? m.ReturnType.GetElementType () : m.ReturnType;

			if (t.IsGenericType || t.ContainsGenericParameters)
				return m.ReturnType.ToString ();

			if (t != null && t.Namespace != null && t.Namespace.StartsWith ("MonoTouch"))
				return "<a href='http://iosapi.xamarin.com/monodoc.ashx?link=T%3a" + t.FullName + "'>" + t.FullName + (m.ReturnType.IsArray ? "[]" : "") + "</a>";

			switch (t.FullName) {
			case "System.Int32":
				return "int";
			case "System.Int64":
				return "long";
			case "System.String":
				return "string";
			case "System.Boolean":
				return "bool";
			case "System.Void":
				return "void";
			case "System.Object":
				return "object";
			case "System.UInt32":
				return "uint";
			case "System.Int16":
				return "short";
			case "System.UInt16":
				return "ushort";
			case "System.UInt64":
				return "ulong";
			case "System.Single":
				return "float";
			case "System.Double":
				return "double";
			case "System.Decimal":
				return "decimal";
			case "System.Char":
				return "char";
			case "System.Byte":
				return "byte";
			case "System.SByte":
				return "sbyte";
			
			}

			return t.FullName;
		}

		public void Run ()
		{
			int count = 0;
			var noCallers = new List<MemberInfo> ();

			var types = (from t in xamarin_assembly.GetTypes ()
			            orderby t.FullName
				select t).ToArray ();

			foreach (var t in types) {
				var methods = t.GetMethods (BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
				List<MethodInfo> privatePinvokes = null;
				var tp = new TypePrinter (t);

				var callers = new Dictionary<MethodInfo,List<MethodInfo>> ();

				foreach (var m in methods) {
					if (m.DeclaringType != t)
						continue;

					var dllattr = m.CustomAttributes.FirstOrDefault (cad => cad.AttributeType == dllimport_type);
					if (dllattr != null){
						if (t.FullName.EndsWith ("ObjCRuntime.Messaging"))
							continue;

						if (m.IsPublic) {
							var entryPoint = m.Name;
							var n = (from x in dllattr.NamedArguments
							         where x.MemberName == "EntryPoint"
							         select x.TypedValue).FirstOrDefault ();

							if (n != null)
								entryPoint = n.Value.ToString ();



							tp.print ("    [Native (\"{0}\")]", entryPoint);
							tp.print ("    {0} {1}<br>", MakeReturn (m), MakeLink (m));
						} else {
							if (privatePinvokes == null)
								privatePinvokes = new List<MethodInfo> ();
							privatePinvokes.Add (m);
						}
					}

					if (t == nsobject_type || t.IsSubclassOf (nsobject_type)) {
						var export = m.CustomAttributes.FirstOrDefault (cad => cad.AttributeType.Name.EndsWith ("ExportAttribute"));
						if (export != null){
							string name;
							tp.print ("    [Selector (\"{0}\")]", export.ConstructorArguments [0].Value.ToString ());

							tp.print ("    {0} {1}<br>", MakeReturn (m), MakeLink (m));
						}
					}
				}

				// If we have private p/invokes, we must scan all methods
				if (privatePinvokes != null) {
					count++;

					foreach (var m in methods) {
						if (m.GetMethodImplementationFlags ().HasFlag (MethodImplAttributes.InternalCall))
							continue;

						if (m.Attributes.HasFlag (MethodAttributes.PinvokeImpl) || m.Attributes.HasFlag (MethodAttributes.Abstract))
							continue;
						foreach (var inst in m.GetInstructions ()) {
							if (inst.OpCode == OpCodes.Call) {
								var target = inst.Operand as MethodInfo;
								if (target != null && target.DeclaringType.Assembly == xamarin_assembly  && target.Attributes.HasFlag (MethodAttributes.PinvokeImpl)) {
									List<MethodInfo> list = null;

									if (!callers.TryGetValue (target, out list)) {
										list = new List<MethodInfo> ();
										list.Add (m);
										callers [target] = list;
									} else
										list.Add (m);
								}
							}
						}

						//
//						foreach (var c in callers) {
//							foreach (var l in c.Value) {
//								tp.print ("    [Native (\"{0}\")]", c.Key.Name);
//								tp.print ("    {0}<br>", l.Name);
//							}
//						}

					}

					//tp.print ("==== STARTING LOOKUP FOR PRIVATE PINVOKES ====");
					foreach (var c in privatePinvokes) {
						if (!callers.ContainsKey (c))
							noCallers.Add (c);
						else {
							foreach (var l in callers [c]) {
								tp.print ("    [Native (\"{0}\")]", c.Name);
								tp.print ("    {0} {1}<br>", MakeReturn (l), MakeLink (l));
							}
						}
					}

				}
				tp.Close ();

			}
			Console.WriteLine ("<p>Total types with pinvokes with private methods: {0}", count);
			Console.WriteLine ("<p>Total types: {0}", types.Length);
			#if false
			// Mostly in constructors
			Console.WriteLine ("<p>Did not find callers for:");
			foreach (var c in noCallers) {
				Console.WriteLine ("<li>{0}.{1}", c.DeclaringType, c);
			}
			#endif
		}
	}

	class MainClass
	{

		public static void Error (string format, params object [] args)
		{
			var txt = String.Format (format, args);
			Console.Error.WriteLine ("Error: {0}", txt);
			Environment.Exit (1);
		}

		public static void Main (string[] args)
		{
			string libraryDir, library;

			if (args.Length == 0)
				libraryDir = "/Developer/MonoTouch/usr/lib/mono/2.1";
			else
				libraryDir = args [0];

			if (args.Length > 1)
				library = args [1];
			else
				library = "monotouch.dll";

			if (!File.Exists (Path.Combine (libraryDir, library)))
				Error ("The file {0} does not exist", libraryDir);

			var scan = new Scanner (libraryDir, library);
			Console.WriteLine ("<html><head><link rel=\"stylesheet\" type=\"text/css\" href=\"mystyle.css\">\n");
			Console.WriteLine ("<style>.member { margin-top: 10px; border: 1px solid #d4d4d4; padding: 10px; font-family: Consolas, Courier, fixed; white-space: pre; display: block; }</style>");
			Console.WriteLine ("</head><body>");
			scan.Run ();

		}
	}
}
