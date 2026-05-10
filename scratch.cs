using System;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;

namespace Test {
    public class Program {
        public static void Main() {
            var props = typeof(GroupTypeId).GetProperties(BindingFlags.Public | BindingFlags.Static);
            foreach (var p in props) {
                if (p.Name.Contains("Rebar") || p.Name.Contains("Reinforce") || p.Name.Contains("Structur") || p.Name.Contains("Armadura")) {
                    Console.WriteLine(p.Name);
                }
            }
        }
    }
}
