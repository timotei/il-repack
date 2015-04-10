﻿//
// Copyright (c) 2011 Francois Valdy
// Copyright (c) 2015 Timotei Dolean
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Collections.Generic;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ILRepacking
{
    internal class RepackImporter : IRepackImporter
    {
        private readonly ILogger _logger;
        private readonly IRepackContext _repackContext;
        private readonly RepackOptions _options;
        private readonly IRepackCopier _repackCopier;
        private readonly Dictionary<AssemblyDefinition, int> _aspOffsets;

        public RepackImporter(ILogger logger, RepackOptions options,
            IRepackCopier repackCopier, IRepackContext repackContext,
            Dictionary<AssemblyDefinition, int> aspOffsets)
        {
            _logger = logger;
            _options = options;
            _repackCopier = repackCopier;
            _repackContext = repackContext;
            _aspOffsets = aspOffsets;
        }

        public void Import(ExportedType type, Collection<ExportedType> col, ModuleDefinition module)
        {
            var nt = new ExportedType(type.Namespace, type.Name, module, type.Scope)
            {
                Attributes = type.Attributes,
                Identifier = type.Identifier, // TODO: CHECK THIS when merging multiple assemblies when exported types ?
                DeclaringType = type.DeclaringType
            };

            // try to skip redirects to merged assemblies
            if (type.Scope is AssemblyNameReference)
            {
                if (_repackContext.MergedAssemblies.Any(x => x.Name.Name == ((AssemblyNameReference)type.Scope).Name))
                {
                    return;
                }
            }
            else if (type.Scope is ModuleReference)
            {
                if (_repackContext.MergedAssemblies.SelectMany(x => x.Modules).Any(x => x.Name == ((ModuleReference)type.Scope).Name))
                {
                    return;
                }
            }
            col.Add(nt);
        }

        public TypeReference Import(TypeReference reference, IGenericParameterProvider context)
        {
            TypeDefinition type = _repackContext.GetMergedTypeFromTypeRef(reference);
            if (type != null)
                return type;

            reference = _repackContext.PlatformFixer.FixPlatformVersion(reference);
            try
            {
                if (context == null)
                {
                    // we come here when importing types used for assembly-level custom attributes
                    return _repackContext.TargetAssemblyMainModule.Import(reference);
                }
                return _repackContext.TargetAssemblyMainModule.Import(reference, context);
            }
            catch (ArgumentOutOfRangeException) // working around a bug in Cecil
            {
                _logger.ERROR("Problem adding reference: " + reference.FullName);
                throw;
            }
        }

        public FieldReference Import(FieldReference reference, IGenericParameterProvider context)
        {
            FieldReference importReference = _repackContext.PlatformFixer.FixPlatformVersion(reference);

            return _repackContext.TargetAssemblyMainModule.Import(importReference, context);
        }

        public MethodReference Import(MethodReference reference)
        {
            MethodReference importReference = _repackContext.PlatformFixer.FixPlatformVersion(reference);
            return _repackContext.TargetAssemblyMainModule.Import(importReference);
        }

        public MethodReference Import(MethodReference reference, IGenericParameterProvider context)
        {
            // If this is a Method/TypeDefinition, it will be corrected to a definition again later

            MethodReference importReference = _repackContext.PlatformFixer.FixPlatformVersion(reference);

            return _repackContext.TargetAssemblyMainModule.Import(importReference, context);
        }

        public TypeDefinition Import(TypeDefinition type, Collection<TypeDefinition> col, bool internalize)
        {
            _logger.VERBOSE("- Importing " + type);

            TypeDefinition nt = _repackContext.TargetAssemblyMainModule.GetType(type.FullName);
            bool justCreatedType = false;
            if (nt == null)
            {
                nt = CreateType(type, col, internalize, null);
                justCreatedType = true;
            }
            else if (DuplicateTypeAllowed(type))
            {
                _logger.INFO("Merging " + type);
            }
            else if (!type.IsPublic || internalize)
            {
                // rename the type previously imported.
                // renaming the new one before import made Cecil throw an exception.
                string other = "<" + Guid.NewGuid() + ">" + nt.Name;
                _logger.INFO("Renaming " + nt.FullName + " into " + other);
                nt.Name = other;
                nt = CreateType(type, col, internalize, null);
                justCreatedType = true;
            }
            else if (_options.UnionMerge)
            {
                _logger.INFO("Merging " + type);
            }
            else
            {
                _logger.ERROR("Duplicate type " + type);
                throw new InvalidOperationException("Duplicate type " + type + " from " + type.Scope + ", was also present in " + _repackContext.MappingHandler.GetOrigTypeModule(nt));
            }
            _repackContext.MappingHandler.StoreRemappedType(type, nt);

            // nested types first (are never internalized)
            foreach (TypeDefinition nested in type.NestedTypes)
                Import(nested, nt.NestedTypes, false);
            foreach (FieldDefinition field in type.Fields)
                CloneTo(field, nt);

            // methods before fields / events
            foreach (MethodDefinition meth in type.Methods)
                CloneTo(meth, nt, justCreatedType);

            foreach (EventDefinition evt in type.Events)
                CloneTo(evt, nt, nt.Events);
            foreach (PropertyDefinition prop in type.Properties)
                CloneTo(prop, nt, nt.Properties);
            return nt;
        }

        // Real stuff below //
        // These methods are somehow a merge between the clone methods of Cecil 0.6 and the import ones of 0.9
        // They use Cecil's MetaDataImporter to rebase imported stuff into the new assembly, but then another pass is required
        //  to clean the TypeRefs Cecil keeps around (although the generated IL would be kind-o valid without, whatever 'valid' means)

        /// <summary>
        /// Clones a field to a newly created type
        /// </summary>
        private void CloneTo(FieldDefinition field, TypeDefinition nt)
        {
            if (nt.Fields.Any(x => x.Name == field.Name))
            {
                _logger.DuplicateIgnored("field", field);
                return;
            }
            FieldDefinition nf = new FieldDefinition(field.Name, field.Attributes, Import(field.FieldType, nt));
            nt.Fields.Add(nf);

            if (field.HasConstant)
                nf.Constant = field.Constant;

            if (field.HasMarshalInfo)
                nf.MarshalInfo = field.MarshalInfo;

            if (field.InitialValue != null && field.InitialValue.Length > 0)
                nf.InitialValue = field.InitialValue;

            if (field.HasLayoutInfo)
                nf.Offset = field.Offset;

            _repackCopier.CopyCustomAttributes(field.CustomAttributes, nf.CustomAttributes, nt);
        }

        /// <summary>
        /// Clones a parameter into a newly created method
        /// </summary>
        private void CloneTo(ParameterDefinition param, MethodDefinition context, Collection<ParameterDefinition> col)
        {
            ParameterDefinition pd = new ParameterDefinition(param.Name, param.Attributes, Import(param.ParameterType, context));
            if (param.HasConstant)
                pd.Constant = param.Constant;
            if (param.HasMarshalInfo)
                pd.MarshalInfo = param.MarshalInfo;
            if (param.HasCustomAttributes)
                _repackCopier.CopyCustomAttributes(param.CustomAttributes, pd.CustomAttributes, context);
            col.Add(pd);
        }

        private void CloneTo(EventDefinition evt, TypeDefinition nt, Collection<EventDefinition> col)
        {
            // ignore duplicate event
            if (nt.Events.Any(x => x.Name == evt.Name))
            {
                _logger.DuplicateIgnored("event", evt);
                return;
            }

            EventDefinition ed = new EventDefinition(evt.Name, evt.Attributes, Import(evt.EventType, nt));
            col.Add(ed);
            if (evt.AddMethod != null)
                ed.AddMethod = FindMethodInNewType(nt, evt.AddMethod);
            if (evt.RemoveMethod != null)
                ed.RemoveMethod = FindMethodInNewType(nt, evt.RemoveMethod);
            if (evt.InvokeMethod != null)
                ed.InvokeMethod = FindMethodInNewType(nt, evt.InvokeMethod);
            if (evt.HasOtherMethods)
            {
                foreach (MethodDefinition meth in evt.OtherMethods)
                {
                    var nm = FindMethodInNewType(nt, meth);
                    if (nm != null)
                        ed.OtherMethods.Add(nm);
                }
            }

            _repackCopier.CopyCustomAttributes(evt.CustomAttributes, ed.CustomAttributes, nt);
        }

        private void CloneTo(PropertyDefinition prop, TypeDefinition nt, Collection<PropertyDefinition> col)
        {
            // ignore duplicate property
            var others = nt.Properties.Where(x => x.Name == prop.Name).ToList();
            if (others.Any())
            {
                bool skip = false;
                if (!IsIndexer(prop) || !IsIndexer(others.First()))
                {
                    skip = true;
                }
                else
                {
                    // "Item" property is used to implement Indexer operators
                    // It may be specified more than one, with extra arguments to get/set methods
                    // Note than one may also define a standard "Item" property, in which case he won't be able to define Indexers

                    // Here we try to prevent duplicate indexers, but allow to merge non-duplicated ones (e.g. this[int] & this[string] )
                    var args = ExtractIndexerParameters(prop);
                    if (others.Any(x => _repackContext.ReflectionHelper.AreSame(args, ExtractIndexerParameters(x))))
                    {
                        skip = true;
                    }
                }
                if (skip)
                {
                    _logger.DuplicateIgnored("property", prop);
                    return;
                }
            }

            PropertyDefinition pd = new PropertyDefinition(prop.Name, prop.Attributes, Import(prop.PropertyType, nt));
            col.Add(pd);
            if (prop.SetMethod != null)
                pd.SetMethod = FindMethodInNewType(nt, prop.SetMethod);
            if (prop.GetMethod != null)
                pd.GetMethod = FindMethodInNewType(nt, prop.GetMethod);
            if (prop.HasOtherMethods)
            {
                foreach (MethodDefinition meth in prop.OtherMethods)
                {
                    var nm = FindMethodInNewType(nt, meth);
                    if (nm != null)
                        pd.OtherMethods.Add(nm);
                }
            }

            _repackCopier.CopyCustomAttributes(prop.CustomAttributes, pd.CustomAttributes, nt);
        }

        private void CloneTo(MethodDefinition meth, TypeDefinition type, bool typeJustCreated)
        {
            // ignore duplicate method for merged duplicated types
            if (!typeJustCreated &&
                type.Methods.Count > 0 &&
                type.Methods.Any(x =>
                  (x.Name == meth.Name) &&
                  (x.Parameters.Count == meth.Parameters.Count) &&
                  (x.ToString() == meth.ToString()))) // TODO: better/faster comparation of parameter types?
            {
                _logger.DuplicateIgnored("method", meth);
                return;
            }
            // use void placeholder as we'll do the return type import later on (after generic parameters)
            MethodDefinition nm = new MethodDefinition(meth.Name, meth.Attributes, _repackContext.TargetAssemblyMainModule.TypeSystem.Void);
            nm.ImplAttributes = meth.ImplAttributes;

            type.Methods.Add(nm);

            _repackCopier.CopyGenericParameters(meth.GenericParameters, nm.GenericParameters, nm);

            if (meth.HasPInvokeInfo)
            {
                if (meth.PInvokeInfo == null)
                {
                    // Even if this was allowed, I'm not sure it'd work out
                    //nm.RVA = meth.RVA;
                }
                else
                {
                    nm.PInvokeInfo = new PInvokeInfo(meth.PInvokeInfo.Attributes, meth.PInvokeInfo.EntryPoint, meth.PInvokeInfo.Module);
                }
            }

            foreach (ParameterDefinition param in meth.Parameters)
                CloneTo(param, nm, nm.Parameters);

            foreach (MethodReference ov in meth.Overrides)
                nm.Overrides.Add(Import(ov, nm));

            _repackCopier.CopySecurityDeclarations(meth.SecurityDeclarations, nm.SecurityDeclarations, nm);
            _repackCopier.CopyCustomAttributes(meth.CustomAttributes, nm.CustomAttributes, nm);

            nm.ReturnType = Import(meth.ReturnType, nm);
            nm.MethodReturnType.Attributes = meth.MethodReturnType.Attributes;
            if (meth.MethodReturnType.HasConstant)
                nm.MethodReturnType.Constant = meth.MethodReturnType.Constant;
            if (meth.MethodReturnType.HasMarshalInfo)
                nm.MethodReturnType.MarshalInfo = meth.MethodReturnType.MarshalInfo;
            if (meth.MethodReturnType.HasCustomAttributes)
                _repackCopier.CopyCustomAttributes(meth.MethodReturnType.CustomAttributes, nm.MethodReturnType.CustomAttributes, nm);

            if (meth.HasBody)
                CloneTo(meth.Body, nm);
            meth.Body = null; // frees memory

            nm.IsAddOn = meth.IsAddOn;
            nm.IsRemoveOn = meth.IsRemoveOn;
            nm.IsGetter = meth.IsGetter;
            nm.IsSetter = meth.IsSetter;
            nm.CallingConvention = meth.CallingConvention;
        }

        private void CloneTo(MethodBody body, MethodDefinition parent)
        {
            MethodBody nb = new MethodBody(parent);
            parent.Body = nb;

            nb.MaxStackSize = body.MaxStackSize;
            nb.InitLocals = body.InitLocals;
            nb.LocalVarToken = body.LocalVarToken;

            foreach (VariableDefinition var in body.Variables)
                nb.Variables.Add(new VariableDefinition(var.Name,
                    Import(var.VariableType, parent)));

            nb.Instructions.SetCapacity(body.Instructions.Count);
            _repackContext.LineIndexer.PreMethodBodyRepack(body, parent);
            foreach (Instruction instr in body.Instructions)
            {
                _repackContext.LineIndexer.ProcessMethodBodyInstruction(instr);

                Instruction ni;

                if (instr.OpCode.Code == Code.Calli)
                {
                    var callSite = (CallSite)instr.Operand;
                    CallSite ncs = new CallSite(Import(callSite.ReturnType, parent))
                    {
                        HasThis = callSite.HasThis,
                        ExplicitThis = callSite.ExplicitThis,
                        CallingConvention = callSite.CallingConvention
                    };
                    foreach (ParameterDefinition param in callSite.Parameters)
                        CloneTo(param, parent, ncs.Parameters);
                    ni = Instruction.Create(instr.OpCode, ncs);
                }
                else switch (instr.OpCode.OperandType)
                    {
                        case OperandType.InlineArg:
                        case OperandType.ShortInlineArg:
                            if (instr.Operand == body.ThisParameter)
                            {
                                ni = Instruction.Create(instr.OpCode, nb.ThisParameter);
                            }
                            else
                            {
                                int param = body.Method.Parameters.IndexOf((ParameterDefinition)instr.Operand);
                                ni = Instruction.Create(instr.OpCode, parent.Parameters[param]);
                            }
                            break;
                        case OperandType.InlineVar:
                        case OperandType.ShortInlineVar:
                            int var = body.Variables.IndexOf((VariableDefinition)instr.Operand);
                            ni = Instruction.Create(instr.OpCode, nb.Variables[var]);
                            break;
                        case OperandType.InlineField:
                            ni = Instruction.Create(instr.OpCode, Import((FieldReference)instr.Operand, parent));
                            break;
                        case OperandType.InlineMethod:
                            ni = Instruction.Create(instr.OpCode, Import((MethodReference)instr.Operand, parent));
                            FixAspNetOffset(nb.Instructions, (MethodReference)instr.Operand, parent);
                            break;
                        case OperandType.InlineType:
                            ni = Instruction.Create(instr.OpCode, Import((TypeReference)instr.Operand, parent));
                            break;
                        case OperandType.InlineTok:
                            if (instr.Operand is TypeReference)
                                ni = Instruction.Create(instr.OpCode, Import((TypeReference)instr.Operand, parent));
                            else if (instr.Operand is FieldReference)
                                ni = Instruction.Create(instr.OpCode, Import((FieldReference)instr.Operand, parent));
                            else if (instr.Operand is MethodReference)
                                ni = Instruction.Create(instr.OpCode, Import((MethodReference)instr.Operand, parent));
                            else
                                throw new InvalidOperationException();
                            break;
                        case OperandType.ShortInlineBrTarget:
                        case OperandType.InlineBrTarget:
                            ni = Instruction.Create(instr.OpCode, (Instruction)instr.Operand); // TODO review
                            break;
                        case OperandType.InlineSwitch:
                            ni = Instruction.Create(instr.OpCode, (Instruction[])instr.Operand); // TODO review
                            break;
                        case OperandType.InlineR:
                            ni = Instruction.Create(instr.OpCode, (double)instr.Operand);
                            break;
                        case OperandType.ShortInlineR:
                            ni = Instruction.Create(instr.OpCode, (float)instr.Operand);
                            break;
                        case OperandType.InlineNone:
                            ni = Instruction.Create(instr.OpCode);
                            break;
                        case OperandType.InlineString:
                            ni = Instruction.Create(instr.OpCode, (string)instr.Operand);
                            break;
                        case OperandType.ShortInlineI:
                            if (instr.OpCode == OpCodes.Ldc_I4_S)
                                ni = Instruction.Create(instr.OpCode, (sbyte)instr.Operand);
                            else
                                ni = Instruction.Create(instr.OpCode, (byte)instr.Operand);
                            break;
                        case OperandType.InlineI8:
                            ni = Instruction.Create(instr.OpCode, (long)instr.Operand);
                            break;
                        case OperandType.InlineI:
                            ni = Instruction.Create(instr.OpCode, (int)instr.Operand);
                            break;
                        default:
                            throw new InvalidOperationException();
                    }
                ni.SequencePoint = instr.SequencePoint;
                nb.Instructions.Add(ni);
            }
            _repackContext.LineIndexer.PostMethodBodyRepack(parent);

            for (int i = 0; i < body.Instructions.Count; i++)
            {
                Instruction instr = nb.Instructions[i];
                if (instr.OpCode.OperandType != OperandType.ShortInlineBrTarget &&
                    instr.OpCode.OperandType != OperandType.InlineBrTarget)
                    continue;

                instr.Operand = GetInstruction(body, nb, (Instruction)body.Instructions[i].Operand);
            }

            foreach (ExceptionHandler eh in body.ExceptionHandlers)
            {
                ExceptionHandler neh = new ExceptionHandler(eh.HandlerType);
                neh.TryStart = GetInstruction(body, nb, eh.TryStart);
                neh.TryEnd = GetInstruction(body, nb, eh.TryEnd);
                neh.HandlerStart = GetInstruction(body, nb, eh.HandlerStart);
                neh.HandlerEnd = GetInstruction(body, nb, eh.HandlerEnd);

                switch (eh.HandlerType)
                {
                    case ExceptionHandlerType.Catch:
                        neh.CatchType = Import(eh.CatchType, parent);
                        break;
                    case ExceptionHandlerType.Filter:
                        neh.FilterStart = GetInstruction(body, nb, eh.FilterStart);
                        break;
                }

                nb.ExceptionHandlers.Add(neh);
            }
        }

        private TypeDefinition CreateType(TypeDefinition type, Collection<TypeDefinition> col, bool internalize, string rename)
        {
            TypeDefinition nt = new TypeDefinition(type.Namespace, rename ?? type.Name, type.Attributes);
            col.Add(nt);

            // only top-level types are internalized
            if (internalize && (nt.DeclaringType == null) && nt.IsPublic)
                nt.IsPublic = false;

            _repackCopier.CopyGenericParameters(type.GenericParameters, nt.GenericParameters, nt);
            if (type.BaseType != null)
                nt.BaseType = Import(type.BaseType, nt);

            if (type.HasLayoutInfo)
            {
                nt.ClassSize = type.ClassSize;
                nt.PackingSize = type.PackingSize;
            }
            // don't copy these twice if UnionMerge==true
            // TODO: we can move this down if we chek for duplicates when adding
            _repackCopier.CopySecurityDeclarations(type.SecurityDeclarations, nt.SecurityDeclarations, nt);
            _repackCopier.CopyTypeReferences(type.Interfaces, nt.Interfaces, nt);
            _repackCopier.CopyCustomAttributes(type.CustomAttributes, nt.CustomAttributes, nt);
            return nt;
        }

        private MethodDefinition FindMethodInNewType(TypeDefinition nt, MethodDefinition methodDefinition)
        {
            var ret = _repackContext.ReflectionHelper.FindMethodDefinitionInType(nt, methodDefinition);
            if (ret == null)
            {
                _logger.WARN("Method '" + methodDefinition.FullName + "' not found in merged type '" + nt.FullName + "'");
            }
            return ret;
        }

        private void FixAspNetOffset(Collection<Instruction> instructions, MethodReference operand, MethodDefinition parent)
        {
            if (operand.Name == "WriteUTF8ResourceString" || operand.Name == "CreateResourceBasedLiteralControl")
            {
                var fullName = operand.FullName;
                if (fullName == "System.Void System.Web.UI.TemplateControl::WriteUTF8ResourceString(System.Web.UI.HtmlTextWriter,System.Int32,System.Int32,System.Boolean)" ||
                    fullName == "System.Web.UI.LiteralControl System.Web.UI.TemplateControl::CreateResourceBasedLiteralControl(System.Int32,System.Int32,System.Boolean)")
                {
                    int offset;
                    if (_aspOffsets.TryGetValue(parent.Module.Assembly, out offset))
                    {
                        int prev = (int)instructions[instructions.Count - 4].Operand;
                        instructions[instructions.Count - 4].Operand = prev + offset;
                    }
                }
            }
        }

        private static bool IsIndexer(PropertyDefinition prop)
        {
            if (prop.Name != "Item")
                return false;
            var parameters = ExtractIndexerParameters(prop);
            return parameters != null && parameters.Count > 0;
        }

        private static IList<ParameterDefinition> ExtractIndexerParameters(PropertyDefinition prop)
        {
            if (prop.GetMethod != null)
                return prop.GetMethod.Parameters;
            if (prop.SetMethod != null)
                return prop.SetMethod.Parameters.ToList().GetRange(0, prop.SetMethod.Parameters.Count - 1);
            return null;
        }

        private static Instruction GetInstruction(MethodBody oldBody, MethodBody newBody, Instruction i)
        {
            int pos = oldBody.Instructions.IndexOf(i);
            if (pos > -1 && pos < newBody.Instructions.Count)
                return newBody.Instructions[pos];

            return null /*newBody.Instructions.Outside*/;
        }

        private bool DuplicateTypeAllowed(TypeDefinition type)
        {
            string fullName = type.FullName;
            // Merging module because IKVM uses this class to store some fields.
            // Doesn't fully work yet, as IKVM is nice enough to give all the fields the same name...
            if (fullName == "<Module>" || fullName == "__<Proxy>")
                return true;

            // XAML helper class, identical in all assemblies, unused within the assembly, and instanciated through reflection from the outside
            // We could just skip them after the first one, but merging them works just fine
            if (fullName == "XamlGeneratedNamespace.GeneratedInternalTypeHelper")
                return true;

            // Merge should be OK since member's names are pretty unique,
            // but renaming duplicate members would be safer...
            if (fullName == "<PrivateImplementationDetails>" && type.IsPublic)
                return true;

            if (_options.AllowedDuplicateTypes.Contains(fullName))
                return true;

            var top = type;
            while (top.IsNested)
                top = top.DeclaringType;
            string nameSpace = top.Namespace;
            if (!String.IsNullOrEmpty(nameSpace) && _options.AllowedDuplicateNameSpaces.Any(s => s == nameSpace || nameSpace.StartsWith(s + ".")))
                return true;

            return false;
        }
    }
}
