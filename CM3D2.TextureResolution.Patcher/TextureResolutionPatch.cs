﻿// --------------------------------------------------
// CM3D2.TextureResolution.Patcher - TextureResolutionPatch.cs
// --------------------------------------------------

using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

using Mono.Cecil;
using Mono.Cecil.Cil;

using ReiPatcher;
using ReiPatcher.Patch;

namespace CM3D2.TextureResolution.Patcher
{
    /*
    * Based on the CM3D2.SkinResolution Patcher in more Update-friendly way and a single patch (Hook-less)
    * Allows for Higher-Resolution skin textures without messing with the texture overlays (wax, etc)
    */

    public class TextureResolutionPatch : PatchBase
    {
        public const string TOKEN = "CM3D2_TEXTURERESOLUTION";
        public override string Name => "CM3D2 Texture Resolution Patch";

        public override bool CanPatch(PatcherArguments args)
        {
            if (args.Assembly.Name.Name != "Assembly-CSharp")
                return false;

            if (GetPatchedAttributes(args.Assembly).Any(attribute => attribute.Info == TOKEN))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Assembly Already Patched");
                Console.ForegroundColor = ConsoleColor.Gray;
                return false;
            }
            return true;
        }

        public override void Patch(PatcherArguments args)
        {
            //Debugger.Launch();
            var mod = args.Assembly.MainModule;
            var ttBody = mod.GetType("TBody");
            var mMulTex = ttBody.Methods.First(def => def.Name == "MulTexProc" && def.HasParameters);

            // Min
            var tInt = typeof(int);
            var mMin = mod.Import(typeof(Math).GetMethods(BindingFlags.Public | BindingFlags.Static)
                                              .Where(info => info.Name == "Min")
                                              .First(
                                                     info =>
                                                     info.GetParameters().Length == 2
                                                     && info.GetParameters()[0].ParameterType == tInt
                                                     && info.GetParameters()[1].ParameterType == tInt));

            // Vector Variables
            var vVecPos = mMulTex.Body.Variables.First(def => def.VariableType.Name == "Vector3");
            var vVecScale = mMulTex.Body.Variables.Last(def => def.VariableType.Name == "Vector3");
            var vVecRatio = new VariableDefinition(vVecScale.VariableType);

            var tVector = vVecPos.VariableType.Resolve();
            var mVectorCtor = mod.Import(tVector.Methods.First(def => def.Name == ".ctor" && def.Parameters.Count == 3));
            var mVectorScale =
                mod.Import(tVector.Methods.First(def => def.Name == "Scale"
                                                        && def.Parameters.All(def2 => def2.ParameterType.Name == "Vector3")));

            mMulTex.Body.Variables.Add(vVecRatio);

            // RenderTexture Methods
            var vRender = mMulTex.Body.Variables.Last(def => def.VariableType.Name == "RenderTexture");
            var tRender = vRender.VariableType.Resolve();

            var mGetWd = tRender.Methods.First(def => def.Name == "get_width");
            var mGetHd = tRender.Methods.First(def => def.Name == "get_height");
            var mGetActived = tRender.Methods.First(def => def.Name == "get_active");
            var mSetActived = tRender.Methods.First(def => def.Name == "set_active");
            var mGetW = mod.Import(mGetWd);
            var mGetH = mod.Import(mGetHd);
            var mGetActive = mod.Import(mGetActived);
            var mSetActive = mod.Import(mSetActived);

            // First Point
            var pointOne =
                mMulTex.Body.Instructions.First(ins => ins.OpCode == OpCodes.Call
                                                       && ((MethodReference) ins.Operand).Name == mSetActive.Name).Next;

            // Second Point
            var pointTwo =
                mMulTex.Body.Instructions.First(ins => ins.OpCode == OpCodes.Call
                                                       && ((MethodReference) ins.Operand).Name == "PushMatrix");

            // Patching
            var ilp = mMulTex.Body.GetILProcessor();

            // Ratio calculated by Min(width,height) / 1024
            // Then matrix translation and scale XY are multiplied by it
            // This block Calculates the ratio, and insantiates a Vector3 with it as the xy components
            ilp.InsertBefore(pointOne, ilp.Create(OpCodes.Ldloca_S, vVecRatio)); // LDLOCA.S _vRATIO
            ilp.InsertBefore(pointOne, ilp.Create(OpCodes.Call, mGetActive)); //--- CALL [UnityEngine.RenderTexture].get_active
            ilp.InsertBefore(pointOne, ilp.Create(OpCodes.Callvirt, mGetW)); //---- CALLVIRT [UnityEngine.RenderTexture].get_width 
            ilp.InsertBefore(pointOne, ilp.Create(OpCodes.Call, mGetActive)); //--- CALL [UnityEngine.RenderTexture].get_active
            ilp.InsertBefore(pointOne, ilp.Create(OpCodes.Callvirt, mGetH)); //---- CALLVIRT [UnityEngine.RenderTexture].get_height
            ilp.InsertBefore(pointOne, ilp.Create(OpCodes.Call, mMin)); //--------- CALL [System.Math].Min
            ilp.InsertBefore(pointOne, ilp.Create(OpCodes.Conv_R4)); //------------ CONV_R4
            ilp.InsertBefore(pointOne, ilp.Create(OpCodes.Ldc_R4, 1024f)); //------ LDC.R4 1024f
            ilp.InsertBefore(pointOne, ilp.Create(OpCodes.Div)); //---------------- DIV
            ilp.InsertBefore(pointOne, ilp.Create(OpCodes.Dup)); //---------------- DUP
            ilp.InsertBefore(pointOne, ilp.Create(OpCodes.Ldc_R4, 1f)); //--------- LDC.R4 1f
            ilp.InsertBefore(pointOne, ilp.Create(OpCodes.Call, mVectorCtor)); //-- CALL [UnityEngine.Vector3].ctor

            // Before calling PushMatrix
            // Scales the translation and position vectors by the ratio vector calculated above
            ilp.InsertBefore(pointTwo, ilp.Create(OpCodes.Ldloc_S, vVecPos)); //--- LDLOC.S _vPOS
            ilp.InsertBefore(pointTwo, ilp.Create(OpCodes.Ldloc_S, vVecRatio)); //- LDLOC.S _vRATIO
            ilp.InsertBefore(pointTwo, ilp.Create(OpCodes.Call, mVectorScale)); //- CALL [UnityEngine.Vector3].Scale
            ilp.InsertBefore(pointTwo, ilp.Create(OpCodes.Stloc_S, vVecPos)); //--- STLOC.S _vPOS

            ilp.InsertBefore(pointTwo, ilp.Create(OpCodes.Ldloc_S, vVecScale)); //- LDLOC.S _vSCALE
            ilp.InsertBefore(pointTwo, ilp.Create(OpCodes.Ldloc_S, vVecRatio)); //- LDLOC.S _vRATIO
            ilp.InsertBefore(pointTwo, ilp.Create(OpCodes.Call, mVectorScale)); //- CALL [UnityEngine.Vector3].Scale
            ilp.InsertBefore(pointTwo, ilp.Create(OpCodes.Stloc_S, vVecScale)); //- STLOC.S _vSCALE
        }

        public override void PrePatch()
        {
            RPConfig.RequestAssembly("Assembly-CSharp.dll");
        }
    }
}
