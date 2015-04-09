//
// Copyright (c) 2015 Timotei Dolean
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//       http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
using Mono.Cecil;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;

namespace ILRepacking
{
    internal class BamlResourceProcessor
    {
        private readonly ModuleDefinition _mainAssembly;
        private readonly IEnumerable<AssemblyDefinition> _mergedAssemblies;
        private readonly Res _resource;

        public BamlResourceProcessor(
            ModuleDefinition mainAssembly,
            IEnumerable<AssemblyDefinition> mergedAssemblies,
            Res resource)
        {
            _mainAssembly = mainAssembly;
            _mergedAssemblies = mergedAssemblies;
            _resource = resource;
        }

        public byte[] GetProcessedResource()
        {
            byte[] streamBytes = _resource.data.Skip(4).ToArray();
            using (var bamlStream = new MemoryStream(streamBytes))
            {
                var bamlTree = LoadBaml(bamlStream);

                IList nodes = bamlTree.GetField<IList>("_nodeList");

                foreach (var node in nodes)
                {
                    ProcessNode(node);
                }

                using (MemoryStream targetStream = new MemoryStream())
                {
                    SerializeBaml(bamlTree, targetStream);
                    targetStream.Position = 0;

                    return BitConverter.GetBytes((int)targetStream.Length).Concat(targetStream.ToArray()).ToArray();
                }
            }
        }

        private void ProcessNode(object node)
        {
            Type nodeType = node.GetType();

            if (nodeType == TypeOf("BamlStartElementNode"))
            {
                string assemblyName = node.GetField<string>("_assemblyName");
                if (_mergedAssemblies.Any(asm => asm.FullName == assemblyName))
                {
                    node.SetField("_assemblyName", _mainAssembly.Name);
                }
            }
        }

        private static Type TypeOf(string globalizationTypeName)
        {
            return Type.GetType(string.Format(
                "MS.Internal.Globalization.{0},{1}",
                globalizationTypeName, typeof(Window).Assembly.FullName));
        }

        private static object LoadBaml(Stream bamlStream)
        {
            MethodInfo loadBamlMethod = TypeOf("BamlResourceDeserializer")
                .GetMethod("LoadBaml", BindingFlags.Static | BindingFlags.NonPublic);

            return loadBamlMethod.Invoke(null, new object[] { bamlStream });
        }

        public static void SerializeBaml(object bamlTree, Stream bamlStream)
        {
            MethodInfo serializeBamlMethod = TypeOf("BamlResourceSerializer")
                .GetMethod("Serialize", BindingFlags.Static | BindingFlags.NonPublic);

            serializeBamlMethod.Invoke(null, new[] { null, bamlTree, bamlStream });
        }
    }

    internal static class ReflectionBasedObjectExtensions
    {
        internal static void SetField(this object instance, string fieldName, object value)
        {
            instance.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(instance, value);
        }

        internal static T GetField<T>(this object instance, string fieldName) where T : class
        {
            return instance.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(instance) as T;
        }
    }
}
