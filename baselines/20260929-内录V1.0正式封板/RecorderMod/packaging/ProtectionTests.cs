using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

// Metadata/IL inspection only: never calls either protected EXE's entry point,
// decrypts its embedded core, or writes beside the release files.
public sealed class ProtectionInspection : MarshalByRefObject
{
    static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
    public string[] Inspect(string file)
    {
        AppDomain.CurrentDomain.ReflectionOnlyAssemblyResolve+=delegate(object sender,ResolveEventArgs args){return Assembly.ReflectionOnlyLoad(args.Name);};
        var assembly=Assembly.ReflectionOnlyLoadFrom(Path.GetFullPath(file));
        Check(assembly.EntryPoint!=null,"Outer EXE has no entry point");
        var names=assembly.GetManifestResourceNames();
        Check(names.Length==1&&names[0]=="aa.encrypted-core","Outer EXE must contain only aa.encrypted-core; found: "+String.Join(", ",names));
        Check(!names.Contains("installer.payload"),"Outer EXE directly exposes the installer ZIP");
        using(var encrypted=assembly.GetManifestResourceStream("aa.encrypted-core")){
            Check(encrypted!=null&&encrypted.Length>=16&&encrypted.Length%16==0,"Encrypted resource length is not a nonempty AES block multiple");
            var zipReadable=false;
            try{using(var archive=new ZipArchive(encrypted,ZipArchiveMode.Read,true)){var count=archive.Entries.Count;zipReadable=true;}}
            catch(InvalidDataException){}
            Check(!zipReadable,"Outer resource is a directly readable ZIP instead of ciphertext");
        }

        var opcodes=new Dictionary<short,OpCode>();
        foreach(var field in typeof(OpCodes).GetFields(BindingFlags.Public|BindingFlags.Static))if(field.FieldType==typeof(OpCode)){var opcode=(OpCode)field.GetValue(null);opcodes[opcode.Value]=opcode;}
        var called=new HashSet<string>();var memoryAssemblyLoad=false;
        foreach(var type in assembly.GetTypes()){
            var methods=new List<MethodBase>();
            methods.AddRange(type.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance|BindingFlags.DeclaredOnly));
            methods.AddRange(type.GetConstructors(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance|BindingFlags.DeclaredOnly));
            if(type.TypeInitializer!=null&&!methods.Contains(type.TypeInitializer))methods.Add(type.TypeInitializer);
            foreach(var method in methods){
                var body=method.GetMethodBody();if(body==null)continue;var il=body.GetILAsByteArray();
                for(var offset=0;offset<il.Length;){
                    short value=il[offset++];if(value==0xfe){Check(offset<il.Length,"Truncated IL opcode");value=unchecked((short)(0xfe00|il[offset++]));}
                    OpCode opcode;Check(opcodes.TryGetValue(value,out opcode),"Unknown IL opcode");
                    if(opcode.OperandType==OperandType.InlineMethod){
                        Check(offset+4<=il.Length,"Truncated IL method token");
                        var target=method.Module.ResolveMethod(BitConverter.ToInt32(il,offset));
                        var owner=target.DeclaringType==null?"":target.DeclaringType.FullName;called.Add(owner+"::"+target.Name);
                        if(owner=="System.Reflection.Assembly"&&target.Name=="Load"){
                            var parameters=target.GetParameters();if(parameters.Length==1&&parameters[0].ParameterType.FullName=="System.Byte[]")memoryAssemblyLoad=true;
                        }
                        Check(!(owner=="System.IO.File"&&(target.Name.StartsWith("Write",StringComparison.Ordinal)||target.Name.StartsWith("Create",StringComparison.Ordinal)||target.Name=="Open"||target.Name=="OpenWrite"||target.Name=="Copy"||target.Name=="Move"||target.Name=="Replace")),"Shell contains a file-persistence API: "+owner+"::"+target.Name);
                        Check(!(owner=="System.IO.Directory"&&target.Name=="CreateDirectory")&&owner!="System.IO.FileStream"&&owner!="System.IO.StreamWriter","Shell contains a disk stream/directory writer: "+owner+"::"+target.Name);
                        Check(!(owner=="System.Reflection.Assembly"&&(target.Name=="LoadFrom"||target.Name=="LoadFile")),"Shell loads the core from a disk path");
                    }
                    int size;
                    switch(opcode.OperandType){
                        case OperandType.InlineNone:size=0;break;
                        case OperandType.ShortInlineBrTarget:case OperandType.ShortInlineI:case OperandType.ShortInlineVar:size=1;break;
                        case OperandType.InlineVar:size=2;break;
                        case OperandType.InlineBrTarget:case OperandType.InlineField:case OperandType.InlineI:case OperandType.InlineMethod:case OperandType.InlineSig:case OperandType.InlineString:case OperandType.InlineTok:case OperandType.InlineType:case OperandType.ShortInlineR:size=4;break;
                        case OperandType.InlineI8:case OperandType.InlineR:size=8;break;
                        case OperandType.InlineSwitch:Check(offset+4<=il.Length,"Truncated IL switch");var count=BitConverter.ToInt32(il,offset);Check(count>=0&&count<=(il.Length-offset-4)/4,"Invalid IL switch table");size=4+4*count;break;
                        default:throw new Exception("Unsupported IL operand "+opcode.OperandType);
                    }
                    Check(offset+size<=il.Length,"Truncated IL operand");offset+=size;
                }
            }
        }
        Check(memoryAssemblyLoad,"Shell does not call Assembly.Load(byte[])");
        Check(called.Contains("System.IO.Compression.GZipStream::.ctor"),"Shell does not decompress the core in memory");
        Check(called.Contains("System.Security.Cryptography.Aes::Create")&&called.Any(x=>x.EndsWith("::CreateDecryptor",StringComparison.Ordinal)),"Shell does not perform AES decryption");
        Check(called.Contains("System.Reflection.Assembly::get_EntryPoint"),"Shell does not resolve the embedded assembly entry point");
        Check(called.Contains("System.AppDomain::SetData"),"Shell does not pass its real host path to the embedded installer/uninstaller");
        return new[]{Path.GetFileName(file)+": only encrypted AES-sized resource; no directly readable installer ZIP",Path.GetFileName(file)+": AES/GZip and Assembly.Load(byte[]) present; no direct file-persistence/core-file-loading APIs in shell IL"};
    }
}

public static class ProtectionTests
{
    public static int Main(string[] args)
    {
        try{
            if(args.Length!=2)throw new ArgumentException("Usage: ProtectionTests.exe protected-installer.exe protected-uninstaller.exe");
            var passed=0;
            foreach(var file in args){
                var domain=AppDomain.CreateDomain("ReadOnlyProtectionInspection-"+Guid.NewGuid().ToString("N"));
                try{
                    var inspector=(ProtectionInspection)domain.CreateInstanceFromAndUnwrap(Assembly.GetExecutingAssembly().Location,typeof(ProtectionInspection).FullName);
                    foreach(var result in inspector.Inspect(file))Console.WriteLine("PASS "+(++passed)+": "+result);
                }finally{AppDomain.Unload(domain);}
            }
            Console.WriteLine("ALL "+passed+" READ-ONLY PROTECTION CHECKS PASSED");return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
