using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace ServiceProtocol
{
    internal static class ServiceProtocolSerializer
    {
        internal static Delegate GeneratePackCode(Type type, Type ownerType, Type delegateType)
        {
            var packMethod = new DynamicMethod("ServiceProtocolSerializerPack_" + type, typeof(void), new[] {typeof(BinaryWriter), ownerType}, type.Module, true);

            var gen = packMethod.GetILGenerator();

            var castedArgument = gen.DeclareLocal(type);

            gen.Emit(OpCodes.Ldarg_1);

            gen.Emit(OpCodes.Castclass, type);

            gen.Emit(OpCodes.Stloc, castedArgument);

            var freeLocals = new Dictionary<Type, Stack<LocalBuilder>>();

            AddPackCode(gen, castedArgument, freeLocals);

            gen.Emit(OpCodes.Ret);

            return packMethod.CreateDelegate(delegateType);
        }

        private static void AddPackCode(ILGenerator gen, LocalBuilder localVar, Dictionary<Type, Stack<LocalBuilder>> freeLocals)
        {
            var type = localVar.LocalType;

            if (type.IsArray)
            {
                var arrayItemType = type.GetElementType();

                if (arrayItemType == typeof(byte))
                {
                    gen.Emit(OpCodes.Ldarg_0);

                    gen.Emit(OpCodes.Ldloc, localVar);

                    gen.Emit(OpCodes.Ldlen);

                    gen.Emit(OpCodes.Conv_I4);

                    gen.Emit(OpCodes.Callvirt, typeof(BinaryWriter).GetMethod("Write", new[] {typeof(int)}));

                    gen.Emit(OpCodes.Ldarg_0);

                    gen.Emit(OpCodes.Ldloc, localVar);

                    gen.Emit(OpCodes.Callvirt, typeof(BinaryWriter).GetMethod("Write", new[] {typeof(byte[])}));

                    return;
                }

                var arrLengthLocal = freeLocals.GetFreeLocal(gen, typeof(int));
                var arrIndex = freeLocals.GetFreeLocal(gen, typeof(int));

                var loopStatementLabel = gen.DefineLabel();
                var loopBodyLabel = gen.DefineLabel();

                gen.Emit(OpCodes.Ldloc, localVar);

                gen.Emit(OpCodes.Ldlen);

                gen.Emit(OpCodes.Conv_I4);

                gen.Emit(OpCodes.Stloc, arrLengthLocal);

                gen.Emit(OpCodes.Ldarg_0);

                gen.Emit(OpCodes.Ldloc, arrLengthLocal);

                gen.Emit(OpCodes.Callvirt, typeof(BinaryWriter).GetMethod("Write", new[] {typeof(int)}));

                gen.Emit(OpCodes.Ldc_I4_0);

                gen.Emit(OpCodes.Stloc, arrIndex);

                gen.Emit(OpCodes.Br, loopStatementLabel);

                //loop body

                gen.MarkLabel(loopBodyLabel);

                if (IsAvailableDirectWriteAndRead(arrayItemType))
                {
                    if (arrayItemType == typeof(IntPtr) || arrayItemType == typeof(UIntPtr)) throw new Exception($"Could not pack \"{arrayItemType}\" type!");

                    if (arrayItemType.IsEnum) arrayItemType = Enum.GetUnderlyingType(arrayItemType);

                    var writeMethod = typeof(BinaryWriter).GetMethod("Write", new[] {arrayItemType});
                    if (writeMethod == null) throw new Exception($"Could not find {nameof(BinaryWriter)} write method by type \"{arrayItemType}\"!");

                    gen.Emit(OpCodes.Ldarg_0);

                    gen.Emit(OpCodes.Ldloc, localVar);

                    gen.Emit(OpCodes.Ldloc, arrIndex);

                    gen.Emit(OpCodes.Ldelem, arrayItemType);

                    gen.Emit(OpCodes.Callvirt, writeMethod);
                }
                else
                {
                    var arrItemLocalVar = freeLocals.GetFreeLocal(gen, arrayItemType);

                    gen.Emit(OpCodes.Ldloc, localVar);

                    gen.Emit(OpCodes.Ldloc, arrIndex);

                    gen.Emit(OpCodes.Ldelem, arrayItemType);

                    gen.Emit(OpCodes.Stloc, arrItemLocalVar);

                    AddPackCode(gen, arrItemLocalVar, freeLocals);

                    freeLocals.ReturnLocal(arrItemLocalVar);
                }

                //loop index inc

                gen.Emit(OpCodes.Ldloc, arrIndex);

                gen.Emit(OpCodes.Ldc_I4_1);

                gen.Emit(OpCodes.Add);

                gen.Emit(OpCodes.Stloc, arrIndex);

                //end of loop body

                //loop statement

                gen.MarkLabel(loopStatementLabel);

                gen.Emit(OpCodes.Ldloc, arrIndex);

                gen.Emit(OpCodes.Ldloc, arrLengthLocal);

                gen.Emit(OpCodes.Blt, loopBodyLabel);

                //end of loop statement

                freeLocals.ReturnLocal(arrIndex);
                freeLocals.ReturnLocal(arrLengthLocal);

                return;
            }

            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).OrderBy(f => f.Name);
            fields = fields.OrderBy(f => !IsAvailableDirectWriteAndRead(f.FieldType));

            foreach (var field in fields)
            {
                if (field.IsDefined(typeof(NonSerializedAttribute))) continue;

                var fieldType = field.FieldType;

                if (IsAvailableDirectWriteAndRead(fieldType))
                {
                    if (fieldType == typeof(IntPtr) || fieldType == typeof(UIntPtr)) throw new Exception($"Could not pack \"{fieldType}\" type!");

                    if (fieldType.IsEnum) fieldType = Enum.GetUnderlyingType(fieldType);

                    var writeMethod = typeof(BinaryWriter).GetMethod("Write", new[] {fieldType});
                    if (writeMethod == null) throw new Exception($"Could not find {nameof(BinaryWriter)} write method by type \"{fieldType}\"!");

                    gen.Emit(OpCodes.Ldarg_0);

                    gen.Emit(OpCodes.Ldloc, localVar);

                    gen.Emit(OpCodes.Ldfld, field);

                    gen.Emit(OpCodes.Callvirt, writeMethod);
                }
                else
                {
                    var fieldLocalVar = freeLocals.GetFreeLocal(gen, fieldType);

                    gen.Emit(OpCodes.Ldloc, localVar);

                    gen.Emit(OpCodes.Ldfld, field);

                    gen.Emit(OpCodes.Stloc, fieldLocalVar);

                    AddPackCode(gen, fieldLocalVar, freeLocals);

                    freeLocals.ReturnLocal(fieldLocalVar);
                }
            }
        }

        internal static Delegate GenerateUnpackCode(Type type, Type ownerType, Type delegateType)
        {
            var unpackMethod = new DynamicMethod("ServiceProtocolSerializerUnpack_" + type, ownerType, new[] {typeof(BinaryReader)}, type.Module, true);

            var gen = unpackMethod.GetILGenerator();

            var freeLocals = new Dictionary<Type, Stack<LocalBuilder>>();

            AddUnpackCode(gen, type, freeLocals);

            gen.Emit(OpCodes.Ret);

            return unpackMethod.CreateDelegate(delegateType);
        }

        private static void AddUnpackCode(ILGenerator gen, Type type, Dictionary<Type, Stack<LocalBuilder>> freeLocals)
        {
            if (type.IsArray)
            {
                var arrayItemType = type.GetElementType();

                if (arrayItemType == typeof(byte))
                {
                    gen.Emit(OpCodes.Ldarg_0);

                    gen.Emit(OpCodes.Dup);

                    gen.Emit(OpCodes.Callvirt, typeof(BinaryReader).GetMethod("ReadInt32", new Type[0]));

                    gen.Emit(OpCodes.Callvirt, typeof(BinaryReader).GetMethod("ReadBytes", new[] {typeof(int)}));

                    return;
                }

                var arrLocal = freeLocals.GetFreeLocal(gen, type);
                var arrLengthLocal = freeLocals.GetFreeLocal(gen, typeof(int));
                var arrIndex = freeLocals.GetFreeLocal(gen, typeof(int));

                var loopStatementLabel = gen.DefineLabel();
                var loopBodyLabel = gen.DefineLabel();

                gen.Emit(OpCodes.Ldarg_0);

                gen.Emit(OpCodes.Callvirt, typeof(BinaryReader).GetMethod("ReadInt32", new Type[0]));

                gen.Emit(OpCodes.Stloc, arrLengthLocal);

                gen.Emit(OpCodes.Ldloc, arrLengthLocal);

                gen.Emit(OpCodes.Newarr, arrayItemType);

                gen.Emit(OpCodes.Stloc, arrLocal);

                gen.Emit(OpCodes.Ldc_I4_0);

                gen.Emit(OpCodes.Stloc, arrIndex);

                gen.Emit(OpCodes.Br, loopStatementLabel);

                //loop body

                gen.MarkLabel(loopBodyLabel);

                gen.Emit(OpCodes.Ldloc, arrLocal);

                gen.Emit(OpCodes.Ldloc, arrIndex);

                if (IsAvailableDirectWriteAndRead(arrayItemType))
                {
                    if (arrayItemType == typeof(IntPtr) || arrayItemType == typeof(UIntPtr)) throw new Exception($"Could not unpack \"{arrayItemType}\" type!");

                    if (arrayItemType.IsEnum) arrayItemType = Enum.GetUnderlyingType(arrayItemType);

                    var readMethod = typeof(BinaryReader).GetMethod("Read" + arrayItemType.Name, new Type[0]);
                    if (readMethod == null) throw new Exception($"Could not find {nameof(BinaryReader)} read method by type \"{arrayItemType}\"!");

                    gen.Emit(OpCodes.Ldarg_0);

                    gen.Emit(OpCodes.Callvirt, readMethod);
                }
                else
                {
                    AddUnpackCode(gen, arrayItemType, freeLocals);
                }

                gen.Emit(OpCodes.Stelem, arrayItemType);

                //loop index inc

                gen.Emit(OpCodes.Ldloc, arrIndex);

                gen.Emit(OpCodes.Ldc_I4_1);

                gen.Emit(OpCodes.Add);

                gen.Emit(OpCodes.Stloc, arrIndex);

                //end of loop body

                //loop statement

                gen.MarkLabel(loopStatementLabel);

                gen.Emit(OpCodes.Ldloc, arrIndex);

                gen.Emit(OpCodes.Ldloc, arrLengthLocal);

                gen.Emit(OpCodes.Blt, loopBodyLabel);

                gen.Emit(OpCodes.Ldloc, arrLocal);

                //end of loop statement

                freeLocals.ReturnLocal(arrIndex);
                freeLocals.ReturnLocal(arrLengthLocal);
                freeLocals.ReturnLocal(arrLocal);

                return;
            }

            var isValueType = type.IsValueType;
            LocalBuilder structLocalVar = null;

            if (isValueType)
            {
                structLocalVar = freeLocals.GetFreeLocal(gen, type);

                gen.Emit(structLocalVar.LocalIndex <= 255 ? OpCodes.Ldloca_S : OpCodes.Ldloca, structLocalVar);

                gen.Emit(OpCodes.Initobj, type);
            }
            else
            {
                var constructor = type.GetConstructor(new Type[0]);
                if (constructor == null) throw new Exception($"Could not find constructor by type \"{type}\"!");

                gen.Emit(OpCodes.Newobj, constructor);
            }

            var fields = type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).OrderBy(f => f.Name);
            fields = fields.OrderBy(f => !IsAvailableDirectWriteAndRead(f.FieldType));

            foreach (var field in fields)
            {
                if (field.IsDefined(typeof(NonSerializedAttribute))) continue;

                var fieldType = field.FieldType;

                if (isValueType) gen.Emit(structLocalVar.LocalIndex <= 255 ? OpCodes.Ldloca_S : OpCodes.Ldloca, structLocalVar);
                else gen.Emit(OpCodes.Dup);

                if (IsAvailableDirectWriteAndRead(fieldType))
                {
                    if (fieldType == typeof(IntPtr) || fieldType == typeof(UIntPtr)) throw new Exception($"Could not unpack \"{fieldType}\" type!");

                    if (fieldType.IsEnum) fieldType = Enum.GetUnderlyingType(fieldType);

                    var readMethod = typeof(BinaryReader).GetMethod("Read" + fieldType.Name, new Type[0]);
                    if (readMethod == null) throw new Exception($"Could not find {nameof(BinaryReader)} read method by type \"{fieldType}\"!");

                    gen.Emit(OpCodes.Ldarg_0);

                    gen.Emit(OpCodes.Callvirt, readMethod);

                    gen.Emit(OpCodes.Stfld, field);
                }
                else
                {
                    AddUnpackCode(gen, fieldType, freeLocals);

                    gen.Emit(OpCodes.Stfld, field);
                }
            }

            if (isValueType)
            {
                gen.Emit(OpCodes.Ldloc, structLocalVar);

                freeLocals.ReturnLocal(structLocalVar);
            }
        }

        private static bool IsAvailableDirectWriteAndRead(Type type)
        {
            return type.IsPrimitive || type == typeof(string) || type == typeof(decimal) || type.IsEnum;
        }

        private static LocalBuilder GetFreeLocal(this Dictionary<Type, Stack<LocalBuilder>> freeLocals, ILGenerator gen, Type type)
        {
            if (!freeLocals.TryGetValue(type, out var locals)) return gen.DeclareLocal(type);
            return locals.Count == 0 ? gen.DeclareLocal(type) : locals.Pop();
        }

        private static void ReturnLocal(this Dictionary<Type, Stack<LocalBuilder>> freeLocals, LocalBuilder local)
        {
            if (!freeLocals.TryGetValue(local.LocalType, out var locals))
            {
                locals = new Stack<LocalBuilder>();

                locals.Push(local);

                freeLocals[local.LocalType] = locals;
            }
            else
            {
                if (locals.Contains(local)) throw new Exception("Free variable already exists! Type: " + local.LocalType);

                locals.Push(local);
            }
        }
    }
}