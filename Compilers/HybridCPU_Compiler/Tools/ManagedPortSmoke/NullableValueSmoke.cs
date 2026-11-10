using HybridCPU.Compiler.Cil;
using HybridCPU.Compiler.Core.IR;
using HybridCPU.Compiler.Core.Target;
using HybridCPU.Compiler.Core.Target.Link;
using HybridCPU.Compiler.Core.Target.Managed;
using HybridCPU.Compiler.Core.Target.Object;
using HybridCPU.Platform.Contracts;

internal static class NullableValueSmoke
{
    public static void RunMapWords()
    {
        byte[] pe=File.ReadAllBytes(typeof(NullableValueFixture).Assembly.Location);
        foreach(bool genericZero in new[]{false,true})foreach(int count in Enumerable.Range(3,6))foreach(bool wide in new[]{false,true})foreach(bool ownerWide in new[]{false,true})foreach(bool transitive in new[]{false,true}) {
            string entry=(genericZero?"ReadStructZero":"ReadStructArity")+count+(wide?"I8":"I4")+(ownerWide?"OwnerI8":"OwnerI4")+(transitive?"Transitive":""),copy=(genericZero?"StructZeroCopy":"StructArityCopy")+count+(wide?"I8":"I4")+(transitive?"Transitive":""),scalarMethod=(genericZero?"ScalarStructZero":"ScalarStructArity")+count+(wide?"I8":"I4"),helper=transitive?"Relay":"Adjust";
            var actual=Compile(entry);var repeat=Compile(entry);var scalar=Compile(scalarMethod);
            if(!actual.RestrictedImage!.PackageBytes.SequenceEqual(repeat.RestrictedImage!.PackageBytes))throw new Exception("Register-bounded helper repeat");
            foreach(int input in new[]{int.MinValue,-1,0,1,int.MaxValue})foreach(long raw in new[]{long.MinValue,(long)int.MinValue,-1L,0L,1L,int.MaxValue,long.MaxValue,1L<<40,(1L<<40)+7}) {
                long delta=wide?raw:unchecked((int)raw);int payload=unchecked(input+(int)delta),fallback=genericZero?0:unchecked((int)delta+count-1);long expected=((long)input<<32)^delta^(input<0?fallback:payload);
                if(Evaluate(actual,entry,unchecked((ulong)(long)input),unchecked((ulong)delta),transitive?4:3)!=unchecked((ulong)expected)||Evaluate(scalar,scalarMethod,unchecked((ulong)(long)input),unchecked((ulong)delta),0)!=unchecked((ulong)expected))throw new Exception("Register-bounded helper scalar/serialized caller preservation/overflow/eager calls");
                ulong packed=input<0?0UL:((ulong)(uint)payload<<1)|1UL,result=((ulong)(uint)(input<0?fallback:payload)<<1)|1UL;
                ulong[] operands=Enumerable.Range(0,count).Select(i=>wide?unchecked((ulong)(delta^((long)(i+1)<<48)^((i+1)*-7L))):unchecked((ulong)(long)(int)(delta^((i+1)*-7L)))).ToArray();
                var image=actual.LinkedImage!;var symbol=image.Symbols.Single(s=>s.Name==actual.MethodObjects.Single(m=>m.MethodIdentity.Contains("."+helper+"(",StringComparison.Ordinal)).MethodIdentity);int calls=0;
                ulong returned=NativeConstantSmoke.Evaluate(image.ImageBytes,operands[0],operands[1],checked((int)(symbol.Address-image.ImageBase)),image.ImageBase,onCall:()=>calls++,remainingArguments:operands.Skip(2).ToArray());
                if(Evaluate(actual,copy,packed,unchecked((ulong)delta),transitive?2:1)!=result||returned!=(genericZero?0UL:operands[^1])||calls!=(transitive?1:0))throw new Exception("Register-bounded independent physical multi-carrier ABI/distinct last operand/high bits/packed result");
            }
            Console.WriteLine("PASS constrained arity "+entry+" independent scalar/serialized registers3-8/I4-I8 ABI/distinct last operand/high bits/presence/overflow/preservation/eager call counts/repeat");
        }
        foreach(bool wide in new[]{false,true})foreach(bool ownerWide in new[]{false,true})foreach(bool transitive in new[]{false,true}) {
            string suffix=(wide?"I8":"I4")+(ownerWide?"OwnerI8":"OwnerI4")+(transitive?"Transitive":""),entry="ReadStructBinary"+suffix,copy="StructBinaryCopy"+(wide?"I8":"I4")+(transitive?"Transitive":""),helper=transitive?"Relay":"Adjust",scalarMethod=wide?"ScalarStructBinaryI8":"ScalarStructBinaryI4";
            var actual=Compile(entry);var repeat=Compile(entry);var scalar=Compile(scalarMethod);
            if(!actual.RestrictedImage!.PackageBytes.SequenceEqual(repeat.RestrictedImage!.PackageBytes))throw new Exception("Binary constrained method image repeat");
            foreach(int input in new[]{int.MinValue,-1,0,1,int.MaxValue})foreach(long raw in new[]{long.MinValue,(long)int.MinValue,-1L,0L,1L,int.MaxValue,long.MaxValue,1L<<40,(1L<<40)+7}) {
                long delta=wide?raw:unchecked((int)raw);int payload=unchecked(input+(int)delta),fallback=unchecked((int)delta+1);long expected=((long)input<<32)^delta^(input<0?fallback:payload);
                if(Evaluate(actual,entry,unchecked((ulong)(long)input),unchecked((ulong)delta),transitive?4:3)!=unchecked((ulong)expected)||Evaluate(scalar,scalarMethod,unchecked((ulong)(long)input),unchecked((ulong)delta),0)!=unchecked((ulong)expected))throw new Exception("Binary constrained independent scalar/serialized caller preservation/overflow/eager calls");
                ulong packed=input<0?0UL:((ulong)(uint)payload<<1)|1UL,result=((ulong)(uint)(input<0?fallback:payload)<<1)|1UL;
                long other=wide?(delta^(1L<<52)^-7L):unchecked((int)(delta^-7L));
                if(Evaluate(actual,copy,packed,unchecked((ulong)delta),transitive?2:1)!=result||Evaluate(actual,helper,unchecked((ulong)delta),unchecked((ulong)other),transitive?1:0)!=unchecked((ulong)other))throw new Exception("Binary constrained independent two-carrier ABI/full width/distinct second argument/packed result");
            }
            Console.WriteLine("PASS binary constrained scalar "+suffix+" independent two-carrier scalar/serialized I4-I8 ABI/second argument/high bits/null/present/extrema/overflow/preservation/eager calls/repeat");
        }
        foreach(bool transitive in new[]{false,true})foreach(bool wideOwner in new[]{false,true}) {
            string entry="ReadStructWide"+(wideOwner?"I8":"I4")+(transitive?"Transitive":""),copy=transitive?"StructWideTransitiveCopy":"StructWideCopy";
            var actual=Compile(entry);var repeat=Compile(entry);var scalar=Compile("ScalarStructWide");
            if(!actual.RestrictedImage!.PackageBytes.SequenceEqual(repeat.RestrictedImage!.PackageBytes))throw new Exception("Constrained I8 method physical repeat");
            foreach(int input in new[]{int.MinValue,-1,0,1,int.MaxValue})foreach(long delta in new[]{long.MinValue,(long)int.MinValue,-1L,0L,1L,int.MaxValue,long.MaxValue,1L<<40,(1L<<40)+7}) {
                int payload=unchecked(input+(int)delta),fallback=unchecked((int)delta);long expected=((long)input<<32)^delta^(input<0?fallback:payload);
                if(Evaluate(actual,entry,unchecked((ulong)(long)input),unchecked((ulong)delta),transitive?4:3)!=unchecked((ulong)expected)||Evaluate(scalar,"ScalarStructWide",unchecked((ulong)(long)input),unchecked((ulong)delta),0)!=unchecked((ulong)expected))throw new Exception("Constrained I8 method independent scalar/serialized wide ABI/high bits/preservation/eager calls");
                ulong packed=input<0?0UL:((ulong)(uint)payload<<1)|1UL,result=((ulong)(uint)(input<0?fallback:payload)<<1)|1UL;
                if(Evaluate(actual,copy,packed,unchecked((ulong)delta),transitive?2:1)!=result||Evaluate(actual,"Adjust",unchecked((ulong)delta),0,transitive?1:0)!=unchecked((ulong)delta))throw new Exception("Constrained I8 method direct full-width signed helper ABI/packed nullable result/eager calls");
            }
            Console.WriteLine("PASS constrained Int64 method "+entry+" independent scalar/serialized full I8 helper and packed nullable ABI/null/present/extrema/high bits/truncation/overflow/input preservation/eager call count/repeat");
        }
        foreach(bool genericMethod in new[]{false,true})foreach(bool transitive in new[]{false,true})foreach(bool wideOwner in new[]{false,true}) {
            string suffix=(wideOwner?"I8":"I4")+(transitive?"Transitive":""),entry="ReadStructScalar"+(genericMethod?"Method":"")+suffix;
            var actual=Compile(entry);var repeat=Compile(entry);string scalarMethod=genericMethod?"ScalarStructScalarMethod":"ScalarStructScalar";var scalar=Compile(scalarMethod);
            if(!actual.RestrictedImage!.PackageBytes.SequenceEqual(repeat.RestrictedImage!.PackageBytes))throw new Exception("Struct scalar physical image repeat");
            foreach(int input in new[]{int.MinValue,-1,0,1,int.MaxValue})foreach(int delta in new[]{int.MinValue,-1,0,1,int.MaxValue}) {
                int payload=unchecked(input+delta),fallback=unchecked(delta+(genericMethod?0:1));long expected=((long)input<<32)^delta^(input<0?fallback:payload);
                if(Evaluate(actual,entry,unchecked((ulong)(long)input),unchecked((ulong)(long)delta),transitive?4:3)!=unchecked((ulong)expected)||Evaluate(scalar,scalarMethod,unchecked((ulong)(long)input),unchecked((ulong)(long)delta),0)!=unchecked((ulong)expected))throw new Exception("Struct scalar independent serialized result/ABI/preservation/eager call count");
                ulong packed=input<0?0UL:((ulong)(uint)payload<<1)|1UL,result=((ulong)(uint)(input<0?fallback:payload)<<1)|1UL;
                if(Evaluate(actual,genericMethod?(transitive?"StructScalarMethodTransitiveCopy":"StructScalarMethodCopy"):transitive?"StructScalarTransitiveCopy":"StructScalarCopy",packed,unchecked((ulong)(long)delta),transitive?2:1)!=result)throw new Exception("Struct scalar direct packed ABI/eager helper calls");
            }
            Console.WriteLine("PASS struct scalar "+(genericMethod?"constrained method ":"")+suffix+" independent scalar/encoded serialized packed ABI/null/present/extrema/high-bit/overflow/input preservation/eager exact call count/repeat");
        }
        foreach(int kind in new[]{0,1,2,3,4,5,6,7,8}) {
            bool presence=kind==1,construction=kind>=2,genericDefault=kind==3,fallback=kind>=4;
            string method=fallback?"ReadGenericFallback":genericDefault?"ReadGenericDefault":construction?"ReadGenericFactory":presence?"ReadGenericPresence":"ReadGenericPayload",copy=fallback?"GenericFallbackCopy":genericDefault?"GenericDefaultCopy":construction?"GenericFactoryCopy":presence?"GenericPresenceCopy":"GenericPayloadCopy",scalarMethod=fallback?"ScalarGenericFallback":construction?"ScalarGenericFactory":"ScalarGenericPayload";var actual=Compile(method);var repeat=Compile(method);var scalar=Compile(scalarMethod);
            if(kind>=5){method=kind==5?"ReadOwnerFallbackI4":"ReadOwnerFallbackI8";copy="OwnerFallbackCopy";actual=Compile(method);repeat=Compile(method);}
            if(kind>=7){method=kind==7?"ReadStructOwnerFallbackI4":"ReadStructOwnerFallbackI8";copy="StructOwnerFallbackCopy";actual=Compile(method);repeat=Compile(method);}
            if(!actual.RestrictedImage!.PackageBytes.SequenceEqual(repeat.RestrictedImage!.PackageBytes))throw new Exception("Generic nullable payload physical repeat");
            foreach(int input in new[]{int.MinValue,-1,0,1,int.MaxValue})foreach(int delta in new[]{int.MinValue,-1,0,1,int.MaxValue}) {
                int payload=unchecked(input+delta);long expected=((long)input<<32)^delta^(input<0?(fallback?delta:construction?0:-7):payload);
                if(Evaluate(actual,method,unchecked((ulong)(long)input),unchecked((ulong)(long)delta),kind>=5?2:1)!=unchecked((ulong)expected)||
                    Evaluate(scalar,scalarMethod,unchecked((ulong)(long)input),unchecked((ulong)(long)delta),0)!=unchecked((ulong)expected))throw new Exception("Generic nullable payload serialized scalar/presence/overflow/ABI/preservation/calls");
                ulong packed=input<0?0UL:((ulong)(uint)payload<<1)|1UL;
                ulong result=construction?((ulong)(uint)(input<0?(fallback?delta:0):payload)<<1)|1UL:packed;
                if(Evaluate(actual,copy,packed,fallback?unchecked((ulong)(long)delta):0,0)!=result)throw new Exception("Generic nullable payload direct packed ABI/serialized operands");
            }
            Console.WriteLine("PASS exact Nullable<T> Copy<int> "+(fallback?"generic fallback argument ":genericDefault?"generic default(T) ":construction?"explicit nullable factory ":presence?"HasValue/copy/null ":"")+"struct-constraint independent scalar/serialized packed ABI/presence/extrema/overflow/preservation/call count/repeat");
        }
        foreach(string method in new[]{"ReadGenericMethodI4","ReadGenericMethodI8","ReadGenericTypeI4","ReadGenericTypeI8","ReadGenericCombinedI4I8","ReadGenericCombinedI8I4"}) {
            var actual=Compile(method);var scalar=Compile("ScalarGenericMap");var repeat=Compile(method);if(!actual.RestrictedImage!.PackageBytes.SequenceEqual(repeat.RestrictedImage!.PackageBytes))throw new Exception("Generic map physical repeat");
            foreach(int input in new[]{int.MinValue,-1,0,1,int.MaxValue})foreach(int delta in new[]{int.MinValue,-1,0,1,int.MaxValue}){long expected=((long)input<<32)^delta^(input<0?-7:unchecked(input+delta+1));if(Evaluate(actual,method,unchecked((ulong)(long)input),unchecked((ulong)(long)delta),input<0?2:3)!=unchecked((ulong)expected)||Evaluate(scalar,"ScalarGenericMap",unchecked((ulong)(long)input),unchecked((ulong)(long)delta),0)!=unchecked((ulong)expected))throw new Exception("Generic map serialized exact contexts/scalar/overflow/ABI/preservation/call count");}
            Console.WriteLine("PASS generic nullable map "+method+" independent scalar/serialized exact method/type/combined contexts/presence/overflow/ABI/preservation/call count/repeat");
        }
        {
            const string method="ReadGenericBoth";var actual=Compile(method);var repeat=Compile(method);if(!actual.RestrictedImage!.PackageBytes.SequenceEqual(repeat.RestrictedImage!.PackageBytes))throw new Exception("Generic coexistence repeat");
            if(actual.MethodObjects.Count(row=>row.MethodIdentity.Contains(".GenericMap",StringComparison.Ordinal))!=2)throw new Exception("Two generic nullable method contexts must remain separate objects");
            foreach(int input in new[]{int.MinValue,-1,0,1,int.MaxValue})foreach(int delta in new[]{int.MinValue,-1,0,1,int.MaxValue}){long expected=((long)input<<32)^delta^(input<0?-7:unchecked(input+2*unchecked(delta+1)));if(Evaluate(actual,method,unchecked((ulong)(long)input),unchecked((ulong)(long)delta),input<0?2:4)!=unchecked((ulong)expected))throw new Exception("Generic coexistence serialized identities/call order/packed result/preservation");}
        }
        ScalarControlFlowV2LinkedProgramV1 Compile(string entry){var graph=new RestrictedCilImporterV1(mode:RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule,pe,"nullable-map-words",[new(typeof(NullableValueFixture).FullName!,entry)],[],MetadataOnlyModules:[new(File.ReadAllBytes(typeof(object).Assembly.Location),"core")]));if(graph.Status!=RestrictedCilImportStatusV1.Success)throw new Exception(string.Join(';',graph.Diagnostics));var linked=new ScalarControlFlowV2ObjectLinkerV1().Link(graph,graph.Graph!.RootIdentities.Single());if(linked.Status!=ScalarControlFlowV2LinkStatusV1.Success)throw new Exception(string.Join(';',linked.Diagnostics));return linked;}
        ulong Evaluate(ScalarControlFlowV2LinkedProgramV1 program,string method,ulong first,ulong second,int expectedCalls){var image=program.LinkedImage!;string identity=program.MethodObjects.Single(m=>m.MethodIdentity.Contains("."+method+"(",StringComparison.Ordinal)).MethodIdentity;var symbol=image.Symbols.Single(s=>s.Name==identity);int calls=0;ulong result=NativeConstantSmoke.Evaluate(image.ImageBytes,first,second,checked((int)(symbol.Address-image.ImageBase)),image.ImageBase,onCall:()=>calls++);if(calls!=expectedCalls)throw new Exception("Nullable map physical call count");return result;}
        foreach((string method,Func<int,int,long> expected) in new (string,Func<int,int,long>)[]{
            (nameof(NullableValueFixture.WordBranch),(input,delta)=>unchecked(input+delta)<0?-7L:11L),
            (nameof(NullableValueFixture.WordZero),(input,delta)=>unchecked(input*delta)==0?3L:9L),
            (nameof(NullableValueFixture.WordUnsigned),(input,delta)=>unchecked((uint)(input+delta))<0x80000001U?5L:13L),
            (nameof(NullableValueFixture.WordCeq),(input,delta)=>unchecked(input+delta)==int.MinValue?1:0),
            (nameof(NullableValueFixture.WordClt),(input,delta)=>unchecked(input+delta)<0?1:0),
            (nameof(NullableValueFixture.WordCltu),(input,delta)=>unchecked((uint)(input+delta))<0x80000001U?1:0),
            (nameof(NullableValueFixture.WordSwitch),(input,delta)=>unchecked(input*delta) switch{0=>23L,1=>29L,2=>31L,3=>37L,_=>41L})}) {
            var actual=Compile(method);var repeat=Compile(method);if(!actual.RestrictedImage!.PackageBytes.SequenceEqual(repeat.RestrictedImage!.PackageBytes))throw new Exception("Word comparison native repeat");
            foreach(int input in new[]{int.MinValue,int.MinValue+1,-65536,-1,0,1,3,65536,int.MaxValue})foreach(int delta in new[]{int.MinValue,-65536,-1,0,1,3,65536,int.MaxValue})if(Evaluate(actual,method,unchecked((ulong)(long)input),unchecked((ulong)(long)delta),0)!=unchecked((ulong)expected(input,delta)))throw new Exception("Word comparison serialized width/overflow/low32/boundary oracle: "+method);
        }
        Console.WriteLine("PASS native I4/U4 word comparisons/brtrue/brfalse/ceq/clt/clt.un/switch after overflow vs independent scalar, signed/unsigned extrema/low32 and repeat");
        foreach(int kind in new[]{0,1,2,3,4,5,6,7}) {
            string suffix=kind switch{0=>"I4",1=>"I8",2=>"U4",3=>"U8",4=>"Helper",5=>"Checked",6=>"Producer",_=>"Switch"},entry="ReadLoop"+suffix,map=kind==6?"ProduceLoopI4":"ShiftLoop"+suffix,scalar=kind is 5 or 6?"ScalarLoopI4":"ScalarLoop"+suffix;
            var actual=Compile(entry);var oracle=Compile(scalar);var repeat=Compile(entry);if(!actual.RestrictedImage!.PackageBytes.SequenceEqual(repeat.RestrictedImage!.PackageBytes))throw new Exception("Counted map physical repeat");
            foreach(int input in new[]{int.MinValue,-1,0,1,int.MaxValue})foreach(int present in new[]{0,1})foreach(long raw in new[]{long.MinValue,(long)int.MinValue,-1L,0L,1L,3L,7L})foreach(int delta in new[]{int.MinValue,-1,0,1,int.MaxValue}) {
                long count=kind==1?raw:unchecked((int)raw);int trips=kind is 2 or 3?3:count>0?(int)count:0,payload=unchecked(input+delta*trips);if(kind==7){payload=input;for(int i=0;i<trips;i++)payload=(i&3) switch{0=>unchecked(payload+delta),1=>unchecked(payload-delta),2=>payload^delta,_=>~payload};}long expected=((long)input<<32)^count^delta^(present==0?-7L:payload);ulong packed=present==0?0UL:((ulong)(uint)input<<1)|1UL,shifted=present==0?0UL:((ulong)(uint)payload<<1)|1UL;
                ulong EvaluateLoop(ScalarControlFlowV2LinkedProgramV1 program,string method,bool direct,int expectedCalls){var image=program.LinkedImage!;var symbol=image.Symbols.Single(s=>s.Name==program.MethodObjects.Single(m=>m.MethodIdentity.Contains("."+method+"(",StringComparison.Ordinal)).MethodIdentity);int calls=0;bool packedIngress=direct&&kind!=6;ulong result=NativeConstantSmoke.Evaluate(image.ImageBytes,packedIngress?packed:unchecked((ulong)(long)input),packedIngress?unchecked((ulong)count):(ulong)present,checked((int)(symbol.Address-image.ImageBase)),image.ImageBase,onCall:()=>calls++,remainingArguments:packedIngress?[unchecked((ulong)(long)delta)]:[unchecked((ulong)count),unchecked((ulong)(long)delta)]);if(calls!=expectedCalls)throw new Exception("Counted map exact helper trip/call count");return result;}
                int helperCalls=kind==4&&present!=0?trips:0;if(EvaluateLoop(actual,entry,false,1+helperCalls)!=unchecked((ulong)expected)||EvaluateLoop(oracle,scalar,false,0)!=unchecked((ulong)expected)||EvaluateLoop(actual,map,true,helperCalls)!=shifted)throw new Exception("Counted map independent closed-form/serialized induction/packed ABI/null/overflow/caller preservation");
            }
            Console.WriteLine("PASS counted nullable map "+suffix+" independent closed-form/serialized backedges/zero-trip/unsigned sign-crossing/high-bit bound/packed return/presence/overflow/4-argument preservation/exact helper trip calls/repeat");
        }
        foreach(bool statement in new[]{false,true}) {
            string suffix=statement?"Statement":"Expression",entry="ReadSwitch"+suffix,map="ShiftSwitch"+suffix,scalar="ScalarSwitch"+suffix;
            var actual=Compile(entry);var oracle=Compile(scalar);var repeat=Compile(entry);if(!actual.RestrictedImage!.PackageBytes.SequenceEqual(repeat.RestrictedImage!.PackageBytes))throw new Exception("Switch map physical repeat");
            foreach(int input in new[]{int.MinValue,-1,0,1,int.MaxValue})foreach(int present in new[]{0,1})foreach(int scale in new[]{int.MinValue,-3,0,1,int.MaxValue})foreach(int selector in new[]{int.MinValue,-1,0,1,2,3,4,int.MaxValue}) {
                int value=present==0?0:input;int? payload=statement?selector switch{0=>null,1=>present==0?null:input,2=>unchecked(value+scale),3=>unchecked(value-scale),_=>unchecked(value*scale)}:unchecked((present==0?selector switch{0=>scale,1=>unchecked(scale+1),2=>unchecked(scale-1),3=>0,_=>selector}:input)*scale);
                long expected=((long)input<<32)^scale^selector^(payload??-7);ulong packed=present==0?0UL:((ulong)(uint)input<<1)|1UL,shifted=payload.HasValue?((ulong)(uint)payload.GetValueOrDefault()<<1)|1UL:0UL;
                ulong EvaluateSwitch(ScalarControlFlowV2LinkedProgramV1 program,string method,bool direct,int expectedCalls){var image=program.LinkedImage!;var symbol=image.Symbols.Single(s=>s.Name==program.MethodObjects.Single(m=>m.MethodIdentity.Contains("."+method+"(",StringComparison.Ordinal)).MethodIdentity);int calls=0;ulong result=NativeConstantSmoke.Evaluate(image.ImageBytes,direct?packed:unchecked((ulong)(long)input),direct?unchecked((ulong)(long)scale):(ulong)present,checked((int)(symbol.Address-image.ImageBase)),image.ImageBase,onCall:()=>calls++,remainingArguments:direct?[unchecked((ulong)(long)selector)]:[unchecked((ulong)(long)scale),unchecked((ulong)(long)selector)]);if(calls!=expectedCalls)throw new Exception("Switch map call count");return result;}
                if(EvaluateSwitch(actual,entry,false,1)!=unchecked((ulong)expected)||EvaluateSwitch(oracle,scalar,false,0)!=unchecked((ulong)expected)||EvaluateSwitch(actual,map,true,0)!=shifted)throw new Exception("Switch map independent scalar/serialized dense/default/ABI/presence/payload/overflow/preservation");
            }
            Console.WriteLine("PASS nullable map switch "+suffix+" independent scalar/serialized branch targets/4-argument ABI/dense/default/null/present-zero/extrema/overflow/packed return/preservation/call count/repeat");
        }
        foreach(bool eight in new[]{false,true})foreach(bool assigned in new[]{false,true})foreach(int expression in new[]{0,1,2}) {
            if(expression==2&&(eight||assigned))continue;
            string suffix=(eight?"Eight":"Three")+(assigned?"Assigned":"")+(expression==1?"Computed":expression==2?"Helper":""),entry="ReadFallback"+suffix,map="ShiftFallback"+suffix,scalar="ScalarFallback"+suffix;
            var actual=Compile(entry);var oracle=Compile(scalar);var repeat=Compile(entry);if(!actual.RestrictedImage!.PackageBytes.SequenceEqual(repeat.RestrictedImage!.PackageBytes))throw new Exception("Fallback map physical repeat");
            foreach(int input in new[]{int.MinValue,-1,0,1,int.MaxValue})foreach(int scale in new[]{int.MinValue,-3,0,1,int.MaxValue})foreach(int fallback in new[]{int.MinValue,-1,0,1,int.MaxValue}) {
                const long delta=(1L<<40)+7;int fallbackValue=unchecked(fallback+(assigned?1:0)+(expression==0?0:1));int payload=unchecked((input==0?fallbackValue:input)*scale);long expected=((long)input<<32)^fallback^(eight?delta:0)^payload;
                ulong EvaluateFallback(ScalarControlFlowV2LinkedProgramV1 program,string method,ulong first,int expectedCalls){var image=program.LinkedImage!;var symbol=image.Symbols.Single(s=>s.Name==program.MethodObjects.Single(m=>m.MethodIdentity.Contains("."+method+"(",StringComparison.Ordinal)).MethodIdentity);int calls=0;ulong[] rest=eight?[(ulong)delta,unchecked((ulong)(long)int.MinValue),7,unchecked((ulong)(long)int.MaxValue),ulong.MaxValue,unchecked((ulong)(long)fallback)]:[unchecked((ulong)(long)fallback)];ulong value=NativeConstantSmoke.Evaluate(image.ImageBytes,first,unchecked((ulong)(long)scale),checked((int)(symbol.Address-image.ImageBase)),image.ImageBase,onCall:()=>calls++,remainingArguments:rest);if(calls!=expectedCalls)throw new Exception("Fallback map physical call count");return value;}
                ulong packed=input==0?0UL:((ulong)(uint)input<<1)|1UL,shifted=((ulong)(uint)payload<<1)|1UL;
                if(EvaluateFallback(actual,entry,unchecked((ulong)(long)input),expression==2?2:1)!=unchecked((ulong)expected)||EvaluateFallback(oracle,scalar,unchecked((ulong)(long)input),0)!=unchecked((ulong)expected)||EvaluateFallback(actual,map,packed,expression==2?1:0)!=shifted)throw new Exception("Fallback map scalar/serialized ABI/current argument SSA/null/result/preservation");
                if(input==0&&EvaluateFallback(actual,map,1,expression==2?1:0)!=1)throw new Exception("Fallback map present-zero must ignore fallback and retain presence");
            }
            Console.WriteLine("PASS nullable fallback map "+suffix+" scalar/serialized a2-a7/current argument SSA/packed presence/overflow/extrema/input preservation/call count/repeat");
        }
        foreach((string entry,string scalar,string map,bool eight) in new[]{("ReadMappedScaled","ScalarMappedScaled","ShiftMappedScaled",false),("ReadMappedEight","ScalarMappedEight","ShiftMappedEight",true)}) {
            var actual=Compile(entry);var oracle=Compile(scalar);var repeat=Compile(entry);if(!actual.RestrictedImage!.PackageBytes.SequenceEqual(repeat.RestrictedImage!.PackageBytes))throw new Exception("Scaled physical map repeat");
            foreach(int input in new[]{int.MinValue,-1,0,1,int.MaxValue})foreach(long delta in new[]{long.MinValue,-1L,0L,1L,long.MaxValue,(1L<<40)+7})foreach(int scale in new[]{int.MinValue,-3,0,1,int.MaxValue}) {
                int payload=unchecked(input*scale+(int)delta);if(eight)payload^=int.MinValue^7^int.MaxValue^-1^3;
                long expected=((long)input<<32)^delta^(input==0?-7L:payload);
                ulong EvaluateScaled(ScalarControlFlowV2LinkedProgramV1 program,string method,ulong first,int expectedCalls){var image=program.LinkedImage!;var symbol=image.Symbols.Single(s=>s.Name==program.MethodObjects.Single(m=>m.MethodIdentity.Contains("."+method+"(",StringComparison.Ordinal)).MethodIdentity);int calls=0;ulong[] rest=eight?[unchecked((ulong)delta),unchecked((ulong)(long)int.MinValue),7,unchecked((ulong)(long)int.MaxValue),ulong.MaxValue,3]:[unchecked((ulong)delta)];ulong result=NativeConstantSmoke.Evaluate(image.ImageBytes,first,unchecked((ulong)(long)scale),checked((int)(symbol.Address-image.ImageBase)),image.ImageBase,onCall:()=>calls++,remainingArguments:rest);if(calls!=expectedCalls)throw new Exception("Scaled physical map call count");return result;}
                ulong packed=input==0?0UL:((ulong)(uint)input<<1)|1UL,shifted=input==0?0UL:((ulong)(uint)payload<<1)|1UL;
                ulong rootResult=EvaluateScaled(actual,entry,unchecked((ulong)(long)input),1),scalarResult=EvaluateScaled(oracle,scalar,unchecked((ulong)(long)input),0),packedResult=EvaluateScaled(actual,map,packed,0);
                if(rootResult!=unchecked((ulong)expected)||scalarResult!=unchecked((ulong)expected)||packedResult!=shifted)throw new Exception($"Scaled map independent scalar/serialized ABI eight={eight} input={input} scale={scale} delta={delta} root={rootResult:x16} scalar={scalarResult:x16} expected={unchecked((ulong)expected):x16} packed={packedResult:x16} expectedPacked={shifted:x16}");
                // Direct map also distinguishes absent from present payload zero.
                if(input==0){int zeroPayload=unchecked((int)delta);if(eight)zeroPayload^=int.MinValue^7^int.MaxValue^-1^3;if(EvaluateScaled(actual,map,1,0)!=(((ulong)(uint)zeroPayload<<1)|1UL))throw new Exception("Scaled present-zero packed ABI");}
            }
            Console.WriteLine("PASS scaled nullable map "+(eight?8:3)+" physical register arguments independent scalar/serialized operands/preservation/full packed return/null/present-zero/extrema/overflow/call count/repeat");
        }
        foreach(int mode in new[]{0,1,2,3,4,5,6}) {
            bool twice=mode==1,fill=mode is 3 or 6,clear=mode==4,getter=mode==5;
            string entry=mode switch{0=>nameof(NullableValueFixture.ReadMapped),1=>nameof(NullableValueFixture.ReadMappedTwice),2=>nameof(NullableValueFixture.ReadMappedAssign),3=>nameof(NullableValueFixture.ReadMappedFill),4=>nameof(NullableValueFixture.ReadMappedClear),5=>nameof(NullableValueFixture.ReadMappedGetter),_=>nameof(NullableValueFixture.ReadMappedInitializerHelper)};
            string scalar=twice?nameof(NullableValueFixture.ScalarMappedTwice):fill?nameof(NullableValueFixture.ScalarMappedFill):clear?nameof(NullableValueFixture.ScalarMappedClear):nameof(NullableValueFixture.ScalarMapped),map=mode switch{0=>"ShiftMapped",1=>"ShiftMappedTwice",2=>"ShiftMappedAssign",3=>"ShiftMappedFill",4=>"ShiftMappedClear",5=>"ShiftMappedGetter",_=>"ShiftMappedInitializerHelper"};
            var actual=Compile(entry);var oracle=Compile(scalar);var repeat=Compile(entry);if(!actual.RestrictedImage!.PackageBytes.SequenceEqual(repeat.RestrictedImage!.PackageBytes))throw new Exception("Nullable map image repeat");
            foreach(int input in new[]{int.MinValue,-1,0,1,int.MaxValue})foreach(int present in new[]{0,1}) {
                bool empty=clear||present==0&&!fill;
                int payload=fill&&present==0?1:unchecked(input+(twice?2:1));long expected=((long)input<<32)^(empty?-7L:payload);ulong packed=present==0?0UL:((ulong)(uint)input<<1)|1UL,shifted=empty?0UL:((ulong)(uint)payload<<1)|1UL;
                if(Evaluate(actual,entry,unchecked((ulong)(long)input),(ulong)present,twice?3:mode==6?2:getter&&present!=0?2:1)!=unchecked((ulong)expected)||Evaluate(oracle,scalar,unchecked((ulong)(long)input),(ulong)present,0)!=unchecked((ulong)expected)||Evaluate(actual,map,packed,0,twice?2:mode==6?1:getter&&present!=0?1:0)!=shifted)throw new Exception("Nullable map nominal argument/return/presence/payload/overflow/preservation oracle");
            }
            Console.WriteLine("PASS nullable map mode="+mode+" independent scalar/serialized words/full packed argument-return ABI/null/present-zero/extrema/overflow/input preservation/argument assignment/call count/repeat");
        }
        foreach(bool wide in new[]{false,true}) {
            string entry=wide?nameof(NullableValueFixture.ReadMappedI8):nameof(NullableValueFixture.ReadMappedI4),scalar=wide?nameof(NullableValueFixture.ScalarMappedI8):nameof(NullableValueFixture.ScalarMappedI4),map=wide?"ShiftMappedI8":"ShiftMappedI4";
            var actual=Compile(entry);var oracle=Compile(scalar);var repeat=Compile(entry);if(!actual.RestrictedImage!.PackageBytes.SequenceEqual(repeat.RestrictedImage!.PackageBytes))throw new Exception("Mixed map repeat");
            foreach(int input in new[]{int.MinValue,-1,0,1,int.MaxValue})foreach(long raw in new[]{long.MinValue,-1L,0L,1L,long.MaxValue,1L<<40,(1L<<40)+7}){
                long delta=wide?raw:unchecked((int)raw);int payload=unchecked(input-(int)delta);long expected=delta^((long)input<<32)^(input==0?-7L:payload);ulong packed=input==0?0UL:((ulong)(uint)input<<1)|1UL,shifted=input==0?0UL:((ulong)(uint)payload<<1)|1UL;
                if(Evaluate(actual,entry,unchecked((ulong)(long)input),unchecked((ulong)delta),1)!=unchecked((ulong)expected)||Evaluate(oracle,scalar,unchecked((ulong)(long)input),unchecked((ulong)delta),0)!=unchecked((ulong)expected)||Evaluate(actual,map,packed,unchecked((ulong)delta),0)!=shifted)throw new Exception("Mixed map packed/signed operand/return/truncation/clobber oracle");
            }
            Console.WriteLine("PASS nullable map signed "+(wide?"I8":"I4")+" operand independent scalar/serialized ABI/full high bits/truncation/operand order/input preservation/packed return/call count/repeat");
        }
    }
    public static void RunReturnHelperWords(bool composition=false)
    {
        byte[] pe=File.ReadAllBytes(typeof(NullableValueFixture).Assembly.Location);
        ScalarControlFlowV2LinkedProgramV1 Compile(string entry){var graph=new RestrictedCilImporterV1(mode:RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule,pe,"nullable-return-helper",[new(typeof(NullableValueFixture).FullName!,entry)],[],MetadataOnlyModules:[new(File.ReadAllBytes(typeof(object).Assembly.Location),"core")]));if(graph.Status!=RestrictedCilImportStatusV1.Success)throw new Exception(string.Join(';',graph.Diagnostics));var linked=new ScalarControlFlowV2ObjectLinkerV1().Link(graph,graph.Graph!.RootIdentities.Single());if(linked.Status!=ScalarControlFlowV2LinkStatusV1.Success)throw new Exception(string.Join(';',linked.Diagnostics));return linked;}
        string entry=composition?nameof(NullableValueFixture.ReadReturnComposition):nameof(NullableValueFixture.ReadReturnHelper),scalar=nameof(NullableValueFixture.ScalarReturnHelper);
        var actual=Compile(entry);var oracle=Compile(scalar);var repeat=Compile(entry);
        if(!actual.RestrictedImage!.PackageBytes.SequenceEqual(repeat.RestrictedImage!.PackageBytes))throw new Exception("Nullable helper repeat image");
        foreach(int input in new[]{int.MinValue,-1,0,1,int.MaxValue}){
            int payload=unchecked(input+1);long expected=((long)input<<32)^(input==0?-7L:payload);
            ulong Evaluate(ScalarControlFlowV2LinkedProgramV1 program,string method,int expectedCalls){var image=program.LinkedImage!;string identity=program.MethodObjects.Single(m=>m.MethodIdentity.Contains("."+method+"(",StringComparison.Ordinal)).MethodIdentity;var symbol=image.Symbols.Single(s=>s.Name==identity);int calls=0;ulong value=NativeConstantSmoke.Evaluate(image.ImageBytes,unchecked((ulong)(long)input),entryOffset:checked((int)(symbol.Address-image.ImageBase)),imageBase:image.ImageBase,onCall:()=>calls++);if(calls!=expectedCalls)throw new Exception("Nullable helper physical call count");return value;}
            if(Evaluate(actual,entry,(input==0?1:3)+(composition?2:0))!=unchecked((ulong)expected)||Evaluate(oracle,scalar,0)!=unchecked((ulong)expected)||Evaluate(actual,"OptionalReturnHelper",input==0?0:2)!=(input==0?0UL:((ulong)(uint)payload<<1)|1UL))throw new Exception("Nullable helper scalar/packed ABI/input preservation/overflow");
            if(composition && Evaluate(actual,"OptionalReturnRelay",input==0?2:4)!=(input==0?0UL:((ulong)(uint)payload<<1)|1UL))throw new Exception("Nullable relay nominal packed return ABI");
        }
        Console.WriteLine(composition?"PASS nullable relay/copy/transitive return serialized words/packed ABI/input preservation/null/present/extrema/overflow/call count/repeat":"PASS nullable producer transitive scalar helper serialized words/packed ABI/input preservation/null/present/extrema/overflow/call count/repeat");
    }
    public static void RunSequential() { foreach(int expression in Enumerable.Range(136,4))RunBinaryHelperWords(expression:expression); }
    public static void RunNested() { foreach(int expression in Enumerable.Range(140,4))RunBinaryHelperWords(expression:expression); }
    public static void RunDependent() { foreach(int expression in Enumerable.Range(144,4))RunBinaryHelperWords(expression:expression); }
    public static void RunAffine() { foreach(int expression in Enumerable.Range(148,4))RunBinaryHelperWords(expression:expression); }
    public static void RunScaled() { foreach(int expression in Enumerable.Range(152,4))RunBinaryHelperWords(expression:expression); }
    public static void RunMixedReturnWords(bool composition=false)
    {
        byte[] pe=File.ReadAllBytes(typeof(NullableValueFixture).Assembly.Location);
        ScalarControlFlowV2LinkedProgramV1 Compile(string entry){var graph=new RestrictedCilImporterV1(mode:RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(ManagedBodyWorldModeV1.StandaloneRestrictedModule,pe,"mixed-return-words",[new(typeof(NullableValueFixture).FullName!,entry)],[],MetadataOnlyModules:[new(File.ReadAllBytes(typeof(object).Assembly.Location),"core")]));if(graph.Status!=RestrictedCilImportStatusV1.Success)throw new Exception(string.Join(';',graph.Diagnostics));var linked=new ScalarControlFlowV2ObjectLinkerV1().Link(graph,graph.Graph!.RootIdentities.Single());if(linked.Status!=ScalarControlFlowV2LinkStatusV1.Success)throw new Exception(string.Join(';',linked.Diagnostics));return linked;}
        foreach(bool reversed in new[]{false,true}) {
            string entry=reversed?nameof(NullableValueFixture.ReadMixedReversed):nameof(NullableValueFixture.ReadMixedReturn);string scalar=reversed?nameof(NullableValueFixture.ScalarMixedReversed):nameof(NullableValueFixture.ScalarMixedReturn);
            if(composition)entry=reversed?nameof(NullableValueFixture.ReadMixedRelayReversed):nameof(NullableValueFixture.ReadMixedRelay);
            var actual=Compile(entry);var oracle=Compile(scalar);var repeat=Compile(entry);if(!actual.RestrictedImage!.PackageBytes.SequenceEqual(repeat.RestrictedImage!.PackageBytes))throw new Exception("Mixed return repeat");
            foreach(int input in new[]{int.MinValue,-1,0,1,int.MaxValue})foreach(long delta in new[]{long.MinValue,-1L,0L,1L,long.MaxValue,1L<<40,(1L<<40)+7}) {
                long payload=input<0?-7:reversed?unchecked((int)delta-input):unchecked(input-(int)delta);
                long expected=delta^((long)input<<32)^payload;
                foreach((ScalarControlFlowV2LinkedProgramV1 program,string name,int count) in new[]{(actual,entry,composition?2:1),(oracle,scalar,0)}){var image=program.LinkedImage!;string identity=program.MethodObjects.Single(m=>m.MethodIdentity.Contains("."+name+"(",StringComparison.Ordinal)).MethodIdentity;var symbol=image.Symbols.Single(s=>s.Name==identity);int calls=0;ulong value=NativeConstantSmoke.Evaluate(image.ImageBytes,unchecked((ulong)(long)input),unchecked((ulong)delta),checked((int)(symbol.Address-image.ImageBase)),image.ImageBase,onCall:()=>calls++);if(value!=unchecked((ulong)expected)||calls!=count)throw new Exception("Mixed I4/I8 argument/return ABI/clobber/truncation");}
            }
            Console.WriteLine((composition?"PASS mixed nullable relay ":"PASS mixed nullable return ")+(reversed?"I8/I4":"I4/I8")+" independent scalar/serialized ABI/full high bits/truncation/operand order/input preservation/call count/repeat");
        }
    }
    public static void RunBinaryReturnWords()
    {
        byte[] pe=File.ReadAllBytes(typeof(NullableValueFixture).Assembly.Location);
        ScalarControlFlowV2LinkedProgramV1 Compile(string entry) {
            var graph=new RestrictedCilImporterV1(mode:RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(
                ManagedBodyWorldModeV1.StandaloneRestrictedModule,pe,"binary-nullable-return-words",[new(typeof(NullableValueFixture).FullName!,entry)],[],MetadataOnlyModules:[new(File.ReadAllBytes(typeof(object).Assembly.Location),"core")]));
            if(graph.Status!=RestrictedCilImportStatusV1.Success)throw new Exception(string.Join(';',graph.Diagnostics));
            var linked=new ScalarControlFlowV2ObjectLinkerV1().Link(graph,graph.Graph!.RootIdentities.Single());if(linked.Status!=ScalarControlFlowV2LinkStatusV1.Success)throw new Exception(string.Join(';',linked.Diagnostics));return linked;
        }
        foreach(int mode in new[]{0,1,2,3}) {
            string entry=mode switch{0=>nameof(NullableValueFixture.ReadBinaryOptional),1=>nameof(NullableValueFixture.ReadBinaryDifference),2=>nameof(NullableValueFixture.ReadBinaryPreserved),_=>nameof(NullableValueFixture.ReadBinaryTwice)};
            string scalar=mode switch{0=>nameof(NullableValueFixture.ScalarBinaryOptional),1=>nameof(NullableValueFixture.ScalarBinaryDifference),2=>nameof(NullableValueFixture.ScalarBinaryPreserved),_=>nameof(NullableValueFixture.ScalarBinaryTwice)};
            var actual=Compile(entry);var oracle=Compile(scalar);var repeat=Compile(entry);
            if(!actual.RestrictedImage!.PackageBytes.SequenceEqual(repeat.RestrictedImage!.PackageBytes))throw new Exception("Binary nullable return repeat");
            foreach(int input in new[]{int.MinValue,-1,0,1,int.MaxValue})foreach(int delta in new[]{int.MinValue,-7,0,1,int.MaxValue}) {
                long sum=input<0?-7:unchecked(input+delta);long difference=input==0?-7:unchecked(delta-input);
                long expected=mode switch{0=>sum,1=>difference,2=>((long)input<<32)^(long)delta^sum,_=>sum+(delta==0?-9L:unchecked(input-delta))};
                ulong Evaluate(ScalarControlFlowV2LinkedProgramV1 program,string name,int expectedCalls) {
                    string identity=program.MethodObjects.Single(m=>m.MethodIdentity.Contains("."+name+"(",StringComparison.Ordinal)).MethodIdentity;
                    var image=program.LinkedImage!;var symbol=image.Symbols.Single(s=>s.Name==identity);int calls=0;
                    ulong value=NativeConstantSmoke.Evaluate(image.ImageBytes,unchecked((ulong)(long)input),unchecked((ulong)(long)delta),checked((int)(symbol.Address-image.ImageBase)),image.ImageBase,onCall:()=>calls++);
                    if(calls!=expectedCalls)throw new Exception("Binary nullable return call count");return value;
                }
                if(Evaluate(actual,entry,mode==3?2:1)!=unchecked((ulong)expected) || Evaluate(oracle,scalar,0)!=unchecked((ulong)expected))throw new Exception("Binary nullable return argument order/physical preservation/result");
                if(mode==0) {
                    ulong packed=input<0?0:((ulong)(uint)unchecked(input+delta)<<1)|1UL;
                    if(Evaluate(actual,"OptionalBinary",0)!=packed)throw new Exception("Binary nullable return packed full-width ABI");
                }
            }
            Console.WriteLine("PASS binary private nullable return mode="+mode+" independent scalar/encoded ABI, argument order/extrema/overflow/input/return preservation/call count/repeat");
        }
    }
    public static void RunReturnWords()
    {
        byte[] pe=File.ReadAllBytes(typeof(NullableValueFixture).Assembly.Location);
        ScalarControlFlowV2LinkedProgramV1 Compile(string method) {
            var graph=new RestrictedCilImporterV1(mode:RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(
                ManagedBodyWorldModeV1.StandaloneRestrictedModule,pe,"nullable-return-words",[new(typeof(NullableValueFixture).FullName!,method)],[],
                MetadataOnlyModules:[new(File.ReadAllBytes(typeof(object).Assembly.Location),"core")]));
            if(graph.Status!=RestrictedCilImportStatusV1.Success)throw new Exception("Nullable return words: "+string.Join(';',graph.Diagnostics));
            var linked=new ScalarControlFlowV2ObjectLinkerV1().Link(graph,graph.Graph!.RootIdentities.Single());
            if(linked.Status!=ScalarControlFlowV2LinkStatusV1.Success)throw new Exception(string.Join(';',linked.Diagnostics));
            return linked;
        }
        foreach((string entry,string scalar,string producer,string packed,bool overflow) in new[]{
            (nameof(NullableValueFixture.ReadOptionalReturn),nameof(NullableValueFixture.ScalarOptionalReturn),"OptionalReturn",nameof(NullableValueFixture.ScalarOptionalPacked),false),
            (nameof(NullableValueFixture.ReadOptionalOverflow),nameof(NullableValueFixture.ScalarOptionalOverflow),"OptionalOverflow",nameof(NullableValueFixture.ScalarOverflowPacked),true)}) {
            var actual=Compile(entry);var oracle=Compile(scalar);var bits=Compile(packed);var repeat=Compile(entry);
            if(!actual.RestrictedImage!.PackageBytes.SequenceEqual(repeat.RestrictedImage!.PackageBytes))throw new Exception("Nullable return words repeat");
            Console.WriteLine("NULLABLE-RETURN-PACKED-BYTES equal="+(actual.MethodObjects.Single(m=>m.MethodIdentity.Contains("."+producer+"(",StringComparison.Ordinal)).CodeSha256==bits.MethodObjects.Single(m=>m.MethodIdentity.Contains("."+packed+"(",StringComparison.Ordinal)).CodeSha256));
            foreach(int input in new[]{int.MinValue,-1,0,1,int.MaxValue}) {
                bool present=overflow?input!=0:input>=0;int payload=overflow?unchecked(input+1):input;
                ulong expectedPacked=present?((ulong)(uint)payload<<1)|1UL:0UL;
                long expected=present?payload:0;
                ulong Evaluate(ScalarControlFlowV2LinkedProgramV1 program,string name,int expectedCalls) {
                    string identity=program.MethodObjects.Single(m=>m.MethodIdentity.Contains("."+name+"(",StringComparison.Ordinal)).MethodIdentity;
                    var image=program.LinkedImage!;var symbol=image.Symbols.Single(s=>s.Name==identity);int calls=0;
                    ulong value=NativeConstantSmoke.Evaluate(image.ImageBytes,unchecked((ulong)(long)input),entryOffset:checked((int)(symbol.Address-image.ImageBase)),imageBase:image.ImageBase,onCall:()=>calls++);
                    if(calls!=expectedCalls)throw new Exception("Nullable return must execute exactly one producer call");return value;
                }
                if(Evaluate(actual,entry,1)!=unchecked((ulong)expected) || Evaluate(oracle,scalar,0)!=unchecked((ulong)expected) ||
                    Evaluate(actual,producer,0)!=expectedPacked || Evaluate(bits,packed,0)!=expectedPacked)throw new Exception("Nullable return independent packed bits/result/ABI");
                long host=overflow?NullableValueFixture.ReadOptionalOverflow(input):NullableValueFixture.ReadOptionalReturn(input);
                if(host!=expected)throw new Exception("Nullable return CoreCLR oracle");
            }
            Console.WriteLine("PASS private Nullable<int> return "+(overflow?"overflow":"conditional")+" encoded packed-I8 return/call/local ABI, independent scalar/bytes/physical operands, present/null/extrema/call count/repeat");
        }
        // Composition keeps an original input and a first packed return live
        // across subsequent calls; equality is semantic, not a byte golden.
        foreach(bool twice in new[]{false,true}) {
            string entry=twice?nameof(NullableValueFixture.ReadOptionalTwice):nameof(NullableValueFixture.ReadOptionalPreserved);
            string scalar=twice?nameof(NullableValueFixture.ScalarOptionalTwice):nameof(NullableValueFixture.ScalarOptionalPreserved);
            var actual=Compile(entry);var oracle=Compile(scalar);
            foreach(int input in new[]{int.MinValue,-1,0,1,int.MaxValue}) {
                long expected=twice?(long)(input<0?-7:input)+(input==0?-9:unchecked(input+1)):((long)input<<40)^(input==0?0:unchecked(input+1));
                foreach((ScalarControlFlowV2LinkedProgramV1 program,string name,int callCount) in new[]{(actual,entry,twice?2:1),(oracle,scalar,0)}) {
                    string identity=program.MethodObjects.Single(m=>m.MethodIdentity.Contains("."+name+"(",StringComparison.Ordinal)).MethodIdentity;
                    var image=program.LinkedImage!;var symbol=image.Symbols.Single(s=>s.Name==identity);int calls=0;
                    ulong value=NativeConstantSmoke.Evaluate(image.ImageBytes,unchecked((ulong)(long)input),entryOffset:checked((int)(symbol.Address-image.ImageBase)),imageBase:image.ImageBase,onCall:()=>calls++);
                    if(value!=unchecked((ulong)expected) || calls!=callCount)throw new Exception("Nullable return input/packed-return preservation across calls");
                }
            }
            Console.WriteLine("PASS private nullable return "+(twice?"two-call":"input")+" preservation and independent loop-free scalar serialized ABI/call count");
        }
    }
    public static void Run()
    {
        RunMapWords();
        RunReturnHelperWords();
        RunReturnHelperWords(true);
        RunMixedReturnWords(true);
        RunMixedReturnWords();
        RunBinaryReturnWords();
        RunReturnWords();
        RunParameterWords();
        RunParameterWords(presence:true);
        RunParameterWords(explicitDefault:true);
        RunParameterWords(dynamicDefault:true);
        RunParameterWords(computedDefault:true);
        RunParameterWords(computedConditional:true);
        RunHelperDefaultWords();
        RunHelperDefaultWords(external:true);
        RunHelperDefaultWords(external:true,constant:true);
        RunBinaryHelperWords();
        RunBinaryHelperWords(generic:true);
        RunBinaryHelperWords(owner:true);
        RunBinaryHelperWords(combined:true);
        RunBinaryHelperWords(lazy:true);
        foreach(int expression in Enumerable.Range(1,155))RunBinaryHelperWords(expression:expression);
        HybridCpuObjectArtifactV1 helper = HybridCpuManagedNullableValueEmitterV1.EmitObject();
        if (helper.Status != HybridCpuObjectStatusV1.Success || HybridCpuManagedAbiFamilyV1.SchemaMinor != 63 ||
            HybridCpuManagedRuntimeEcallContractV1.NullableValueInt64CheckedOperation != 40)
            throw new Exception("Nullable helper ABI identity");
        byte[] bytes = helper.Sections.Single(s => s.Name == ".text").Data;
        List<HybridCpuInstructionWord> words = new List<HybridCpuInstructionWord>();
        for (int offset = 0; offset < bytes.Length; offset += HybridCpuBundleSerializer.BundleSizeBytes) {
            HybridCpuInstructionBundle bundle = new HybridCpuInstructionBundle();
            if (!bundle.TryReadBytes(bytes, offset)) throw new Exception("Nullable helper serialized bundle");
            words.Add(bundle.GetInstruction(0));
        }
        bool Has(HybridCpuOpcode opcode, byte rd, byte rs, short immediate) => words.Any(word => (HybridCpuOpcode)word.OpCode == opcode &&
            HybridCpuInstructionWord.TryUnpackArchRegs(word.Word1, out byte actualRd, out byte actualRs, out _) && actualRd == rd && actualRs == rs && word.Immediate == unchecked((ushort)immediate));
        if (words.Count(w => (HybridCpuOpcode)w.OpCode == HybridCpuOpcode.ECALL) != 1 ||
            !Has(HybridCpuOpcode.ADDI, 16, 10, 0) || !Has(HybridCpuOpcode.ADDI, 18, 11, 0) ||
            !Has(HybridCpuOpcode.ADDI, 11, 0, 40) || !Has(HybridCpuOpcode.ADDI, 15, 0, 2) ||
            !Has(HybridCpuOpcode.ADDI, 2, 2, -16) || !Has(HybridCpuOpcode.ADDI, 2, 2, 16) ||
            !words.Any(w => (HybridCpuOpcode)w.OpCode == HybridCpuOpcode.BNE &&
                HybridCpuInstructionWord.TryUnpackArchRegs(w.Word1, out _, out byte rs1, out byte rs2) && rs1 == 12 && rs2 == 0))
            throw new Exception("Nullable helper physical ECALL arguments/operation/count/frame/managed-throw branch");
        byte[] pe = File.ReadAllBytes(typeof(NullableValueFixture).Assembly.Location);
        ManagedBodyWorldV1 request = new ManagedBodyWorldV1(ManagedBodyWorldModeV1.StandaloneRestrictedModule, pe, "nullable-value-smoke",
            [new(typeof(NullableValueFixture).FullName!, nameof(NullableValueFixture.Read))], [],
            MetadataOnlyModules: [new(File.ReadAllBytes(typeof(object).Assembly.Location), "core")]);
        ManagedCallGraphCompilationV1 graph = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(request);
        if (graph.Status != RestrictedCilImportStatusV1.Success) throw new Exception(string.Join(';', graph.Diagnostics));
        ScalarControlFlowV2LinkedProgramV1 linked = new ScalarControlFlowV2ObjectLinkerV1().Link(graph, graph.Graph!.RootIdentities.Single());
        if (linked.Status != ScalarControlFlowV2LinkStatusV1.Success ||
            linked.LinkedRuntimeModuleIdentities?.Contains(HybridCpuManagedNullableValueEmitterV1.ModuleIdentity) != true ||
            !graph.Methods.Single().Import.RequiresNativeExceptionTransfer ||
            graph.TypeUniverse?.Rows.Any(r => r.StableIdentity == "System.InvalidOperationException" && r.Descriptor is not null) != true)
            throw new Exception("Nullable helper exact exception metadata/native EH/image: " + string.Join(';', linked.Diagnostics));
        ManagedCallGraphCompilationV1 noMetadata = new RestrictedCilImporterV1(mode: RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(request with { MetadataOnlyModules = [] });
        if (noMetadata.Status == RestrictedCilImportStatusV1.Success &&
            new ScalarControlFlowV2ObjectLinkerV1().Link(noMetadata, noMetadata.Graph!.RootIdentities.Single()).Status == ScalarControlFlowV2LinkStatusV1.Success)
            throw new Exception("Nullable helper linked without exception metadata authority");
        Console.WriteLine("PASS checked nullable Value exact helper ABI/serialized ECALL/full-width args/frame/throw marker and exception-metadata/native-EH image gate");
    }
    private static void RunBinaryHelperWords(bool generic = false, bool owner = false, bool combined = false, bool lazy = false, int expression = 0)
    {
        byte[] primary=File.ReadAllBytes(typeof(NullableValueFixture).Assembly.Location);
        byte[] dependency=File.ReadAllBytes(typeof(ManagedPortSmoke.Dependency.MetadataOnlyCallee).Assembly.Location);
        ScalarControlFlowV2LinkedProgramV1 Compile(string root) {
            ManagedCallGraphCompilationV1 graph =new RestrictedCilImporterV1(mode:RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(
                ManagedBodyWorldModeV1.StandaloneRestrictedModule,primary,"binary-nullable-words",[new(typeof(NullableValueFixture).FullName!,root)],[],
                DependencyModules:[new(dependency,"selected-binary-helper")],MetadataOnlyModules:[new(File.ReadAllBytes(typeof(object).Assembly.Location),"core")]));
            if(graph.Status!=RestrictedCilImportStatusV1.Success)throw new Exception(string.Join(';',graph.Diagnostics));
            ScalarControlFlowV2LinkedProgramV1 linked =new ScalarControlFlowV2ObjectLinkerV1().Link(graph,graph.Graph!.RootIdentities.Single());
            if(linked.Status!=ScalarControlFlowV2LinkStatusV1.Success)throw new Exception(string.Join(';',linked.Diagnostics));
            return linked;
        }
        string entryName=lazy?nameof(NullableValueFixture.ReadLazyHelper):combined?nameof(NullableValueFixture.ReadCombinedHelper):owner?nameof(NullableValueFixture.ReadOwnerHelper):generic?nameof(NullableValueFixture.ReadGenericHelper):nameof(NullableValueFixture.ReadBinaryHelper);
        string scalarEntry=lazy?nameof(NullableValueFixture.ReadLazyHelperScalar):combined?nameof(NullableValueFixture.ReadCombinedHelperScalar):owner?nameof(NullableValueFixture.ReadOwnerHelperScalar):generic?nameof(NullableValueFixture.ReadGenericHelperScalar):nameof(NullableValueFixture.ReadBinaryHelperScalar);
        string getter=lazy?"ConsumeLazyHelper":combined?"ConsumeCombinedHelper":owner?"ConsumeOwnerHelper":generic?"ConsumeGenericHelper":"ConsumeBinaryHelper";
        if(expression>0) {
            entryName=expression==1?nameof(NullableValueFixture.ReadLazyDynamic):expression==2?nameof(NullableValueFixture.ReadLazyAdd):nameof(NullableValueFixture.ReadLazyMask);
            scalarEntry=expression==1?nameof(NullableValueFixture.ScalarLazyDynamic):expression==2?nameof(NullableValueFixture.ScalarLazyAdd):nameof(NullableValueFixture.ScalarLazyMask);
            getter=expression==1?"ConsumeLazyDynamic":expression==2?"ConsumeLazyAdd":"ConsumeLazyMask";
            if(expression>=4) {
                entryName=expression==4?nameof(NullableValueFixture.ReadLazyConditional):expression==5?nameof(NullableValueFixture.ReadLazyConditionalHelper):expression==6?nameof(NullableValueFixture.ReadLazyLogical):nameof(NullableValueFixture.ReadLazyTwoHelpers);
                scalarEntry=expression==4?nameof(NullableValueFixture.ScalarLazyConditional):expression==5?nameof(NullableValueFixture.ScalarLazyConditionalHelper):expression==6?nameof(NullableValueFixture.ScalarLazyLogical):nameof(NullableValueFixture.ScalarLazyTwoHelpers);
                getter=expression==4?"ConsumeLazyConditional":expression==5?"ConsumeLazyConditionalHelper":expression==6?"ConsumeLazyLogical":"ConsumeLazyTwoHelpers";
            }
        }
        if(expression is 8 or 9) {
            entryName=expression==8?nameof(NullableValueFixture.ReadExplicitPresence):nameof(NullableValueFixture.ReadExplicitConditional);
            scalarEntry=expression==8?nameof(NullableValueFixture.ScalarLazyDynamic):nameof(NullableValueFixture.ScalarLazyConditionalHelper);
            getter=expression==8?"ConsumeExplicitPresence":"ConsumeExplicitConditional";
        }
        if(expression is 10 or 11) {
            entryName=expression==10?nameof(NullableValueFixture.ReadGuardedValue):nameof(NullableValueFixture.ReadGuardedConditional);
            scalarEntry=expression==10?nameof(NullableValueFixture.ScalarLazyDynamic):nameof(NullableValueFixture.ScalarLazyConditionalHelper);
            getter=expression==10?"ConsumeGuardedValue":"ConsumeGuardedConditional";
        }
        if(expression is 12 or 13) {
            entryName=expression==12?nameof(NullableValueFixture.ReadEarlyReturn):nameof(NullableValueFixture.ReadEarlyConditional);
            scalarEntry=expression==12?nameof(NullableValueFixture.ScalarLazyDynamic):nameof(NullableValueFixture.ScalarLazyConditionalHelper);
            getter=expression==12?"ConsumeEarlyReturn":"ConsumeEarlyConditional";
        }
        if(expression is 14 or 15) {
            entryName=expression==14?nameof(NullableValueFixture.ReadPresentFirst):nameof(NullableValueFixture.ReadPresentConditional);
            scalarEntry=expression==14?nameof(NullableValueFixture.ScalarLazyDynamic):nameof(NullableValueFixture.ScalarLazyConditionalHelper);
            getter=expression==14?"ConsumePresentFirst":"ConsumePresentConditional";
        }
        if(expression is 16 or 17) {
            entryName=expression==16?nameof(NullableValueFixture.ReadPayloadAdd):nameof(NullableValueFixture.ReadPayloadMask);
            scalarEntry=expression==16?nameof(NullableValueFixture.ScalarPayloadAdd):nameof(NullableValueFixture.ScalarPayloadMask);
            getter=expression==16?"ConsumePayloadAdd":"ConsumePayloadMask";
        }
        if(expression is 18 or 19) {
            entryName=expression==18?nameof(NullableValueFixture.ReadTrailingAdd):nameof(NullableValueFixture.ReadTrailingMask);
            scalarEntry=expression==18?nameof(NullableValueFixture.ScalarPayloadAdd):nameof(NullableValueFixture.ScalarPayloadMask);
            getter=expression==18?"ConsumeTrailingAdd":"ConsumeTrailingMask";
        }
        if(expression is 20 or 21) {
            entryName=expression==20?nameof(NullableValueFixture.ReadMergedAdd):nameof(NullableValueFixture.ReadMergedMask);
            scalarEntry=expression==20?nameof(NullableValueFixture.ScalarPayloadAdd):nameof(NullableValueFixture.ScalarPayloadMask);
            getter=expression==20?"ConsumeMergedAdd":"ConsumeMergedMask";
        }
        if(expression>=22) {
            entryName=expression==22?nameof(NullableValueFixture.ReadRepeatTrailing):expression==23?nameof(NullableValueFixture.ReadRepeatPresent):nameof(NullableValueFixture.ReadRepeatMerged);
            scalarEntry=nameof(NullableValueFixture.ScalarRepeatPayload);
            getter=expression==22?"ConsumeRepeatTrailing":expression==23?"ConsumeRepeatPresent":"ConsumeRepeatMerged";
        }
        if(expression>=25) {
            entryName=expression==25?nameof(NullableValueFixture.ReadLocalTrailing):expression==26?nameof(NullableValueFixture.ReadLocalPresent):nameof(NullableValueFixture.ReadBothLocals);
            scalarEntry=expression==27?nameof(NullableValueFixture.ScalarBothLocals):nameof(NullableValueFixture.ScalarLocalPayload);
            getter=expression==25?"ConsumeLocalTrailing":expression==26?"ConsumeLocalPresent":"ConsumeBothLocals";
        }
        if(expression>=28) {
            entryName=expression==28?nameof(NullableValueFixture.ReadCounted3):expression==29?nameof(NullableValueFixture.ReadCountedPresent3):expression==30?nameof(NullableValueFixture.ReadCounted5):nameof(NullableValueFixture.ReadCounted0);
            scalarEntry=expression<=29?nameof(NullableValueFixture.ScalarCounted3):expression==30?nameof(NullableValueFixture.ScalarCounted5):nameof(NullableValueFixture.ScalarCounted0);
            getter=expression==28?"ConsumeCounted3":expression==29?"ConsumeCountedPresent3":expression==30?"ConsumeCounted5":"ConsumeCounted0";
        }
        if(expression>=32) {
            entryName=expression==32?nameof(NullableValueFixture.ReadCountedCall):nameof(NullableValueFixture.ReadCountedPresentCall);
            scalarEntry=nameof(NullableValueFixture.ScalarCountedCall);
            getter=expression==32?"ConsumeCountedCall":"ConsumeCountedPresentCall";
        }
        if(expression>=34) {
            entryName=expression switch {34=>nameof(NullableValueFixture.ReadCountedGetter3),35=>nameof(NullableValueFixture.ReadCountedPresentGetter3),36=>nameof(NullableValueFixture.ReadCountedGetter0),37=>nameof(NullableValueFixture.ReadCountedGetter5),38=>nameof(NullableValueFixture.ReadCountedGetterCall),_=>nameof(NullableValueFixture.ReadCountedPresentGetterCall)};
            scalarEntry=expression switch {36=>nameof(NullableValueFixture.ScalarCountedGetter0),37=>nameof(NullableValueFixture.ScalarCountedGetter5),>=38=>nameof(NullableValueFixture.ScalarCountedGetterCall),_=>nameof(NullableValueFixture.ScalarCountedGetter3)};
            getter=expression switch {34=>"ConsumeCountedGetter3",35=>"ConsumeCountedPresentGetter3",36=>"ConsumeCountedGetter0",37=>"ConsumeCountedGetter5",38=>"ConsumeCountedGetterCall",_=>"ConsumeCountedPresentGetterCall"};
        }
        if(expression>=40) {
            entryName=expression switch {40=>nameof(NullableValueFixture.ReadLoopIf),41=>nameof(NullableValueFixture.ReadLoopPresentIf),42=>nameof(NullableValueFixture.ReadLoopCallIf),43=>nameof(NullableValueFixture.ReadLoopPresentCallIf),44=>nameof(NullableValueFixture.ReadLoopPhi),_=>nameof(NullableValueFixture.ReadLoopCallPhi)};
            scalarEntry=expression switch {40 or 41=>nameof(NullableValueFixture.ScalarLoopIf),42 or 43=>nameof(NullableValueFixture.ScalarLoopCallIf),44=>nameof(NullableValueFixture.ScalarLoopPhi),_=>nameof(NullableValueFixture.ScalarLoopCallPhi)};
            getter=expression switch {40=>"ConsumeLoopIf",41=>"ConsumeLoopPresentIf",42=>"ConsumeLoopCallIf",43=>"ConsumeLoopPresentCallIf",44=>"ConsumeLoopPhi",_=>"ConsumeLoopCallPhi"};
        }
        if(expression>=46) {
            entryName=expression switch {46=>nameof(NullableValueFixture.ReadLoopBreak),47=>nameof(NullableValueFixture.ReadLoopPresentBreak),48=>nameof(NullableValueFixture.ReadLoopCallBreak),49=>nameof(NullableValueFixture.ReadLoopPresentCallBreak),50=>nameof(NullableValueFixture.ReadLoopIndexBreak),_=>nameof(NullableValueFixture.ReadLoopCallIndexBreak)};
            scalarEntry=expression switch {46 or 47=>nameof(NullableValueFixture.ScalarLoopBreak),48 or 49=>nameof(NullableValueFixture.ScalarLoopCallBreak),50=>nameof(NullableValueFixture.ScalarLoopIndexBreak),_=>nameof(NullableValueFixture.ScalarLoopCallIndexBreak)};
            getter=expression switch {46=>"ConsumeLoopBreak",47=>"ConsumeLoopPresentBreak",48=>"ConsumeLoopCallBreak",49=>"ConsumeLoopPresentCallBreak",50=>"ConsumeLoopIndexBreak",_=>"ConsumeLoopCallIndexBreak"};
        }
        if(expression>=52) {
            entryName=expression switch {52=>nameof(NullableValueFixture.ReadLoopReturn),53=>nameof(NullableValueFixture.ReadLoopPresentReturn),54=>nameof(NullableValueFixture.ReadLoopCallReturn),55=>nameof(NullableValueFixture.ReadLoopPresentCallReturn),56=>nameof(NullableValueFixture.ReadLoopIndexReturn),_=>nameof(NullableValueFixture.ReadLoopCallIndexReturn)};
            scalarEntry=expression switch {52 or 53=>nameof(NullableValueFixture.ScalarLoopReturn),54 or 55=>nameof(NullableValueFixture.ScalarLoopCallReturn),56=>nameof(NullableValueFixture.ScalarLoopIndexReturn),_=>nameof(NullableValueFixture.ScalarLoopCallIndexReturn)};
            getter=expression switch {52=>"ConsumeLoopReturn",53=>"ConsumeLoopPresentReturn",54=>"ConsumeLoopCallReturn",55=>"ConsumeLoopPresentCallReturn",56=>"ConsumeLoopIndexReturn",_=>"ConsumeLoopCallIndexReturn"};
        }
        if(expression>=58) {
            entryName=expression switch {58=>nameof(NullableValueFixture.ReadWideReturn),59=>nameof(NullableValueFixture.ReadPresentWideReturn),60=>nameof(NullableValueFixture.ReadCallWideReturn),61=>nameof(NullableValueFixture.ReadPresentCallWideReturn),62=>nameof(NullableValueFixture.ReadIndexWideReturn),63=>nameof(NullableValueFixture.ReadCallIndexWideReturn),64=>nameof(NullableValueFixture.ReadHighReturn),65=>nameof(NullableValueFixture.ReadCallIndexHighReturn),66=>nameof(NullableValueFixture.ReadNormalWideReturn),_=>nameof(NullableValueFixture.ReadCallIndexNormalWideReturn)};
            scalarEntry=expression switch {58 or 59=>nameof(NullableValueFixture.ScalarWideReturn),60 or 61=>nameof(NullableValueFixture.ScalarCallWideReturn),62=>nameof(NullableValueFixture.ScalarIndexWideReturn),63=>nameof(NullableValueFixture.ScalarCallIndexWideReturn),64=>nameof(NullableValueFixture.ScalarHighReturn),65=>nameof(NullableValueFixture.ScalarCallIndexHighReturn),66=>nameof(NullableValueFixture.ScalarNormalWideReturn),_=>nameof(NullableValueFixture.ScalarCallIndexNormalWideReturn)};
            getter=expression switch {58=>"ConsumeWideReturn",59=>"ConsumePresentWideReturn",60=>"ConsumeCallWideReturn",61=>"ConsumePresentCallWideReturn",62=>"ConsumeIndexWideReturn",63=>"ConsumeCallIndexWideReturn",64=>"ConsumeHighReturn",65=>"ConsumeCallIndexHighReturn",66=>"ConsumeNormalWideReturn",_=>"ConsumeCallIndexNormalWideReturn"};
        }
        if(expression>=68) {
            entryName=expression switch {68=>nameof(NullableValueFixture.ReadI8LocalReturn),69=>nameof(NullableValueFixture.ReadPresentI8LocalReturn),70=>nameof(NullableValueFixture.ReadCallI8LocalReturn),71=>nameof(NullableValueFixture.ReadPresentCallI8LocalReturn),72=>nameof(NullableValueFixture.ReadIndexI8LocalReturn),73=>nameof(NullableValueFixture.ReadCallIndexI8LocalReturn),74=>nameof(NullableValueFixture.ReadHighI8LocalReturn),75=>nameof(NullableValueFixture.ReadCallIndexNormalI8LocalReturn),76=>nameof(NullableValueFixture.ReadI8LocalPhi),_=>nameof(NullableValueFixture.ReadCallI8LocalPhi)};
            scalarEntry=expression switch {68 or 69=>nameof(NullableValueFixture.ScalarI8LocalReturn),70 or 71=>nameof(NullableValueFixture.ScalarCallI8LocalReturn),72=>nameof(NullableValueFixture.ScalarIndexI8LocalReturn),73=>nameof(NullableValueFixture.ScalarCallIndexI8LocalReturn),74=>nameof(NullableValueFixture.ScalarHighI8LocalReturn),75=>nameof(NullableValueFixture.ScalarCallIndexNormalI8LocalReturn),76=>nameof(NullableValueFixture.ScalarI8LocalPhi),_=>nameof(NullableValueFixture.ScalarCallI8LocalPhi)};
            getter=expression switch {68=>"ConsumeI8LocalReturn",69=>"ConsumePresentI8LocalReturn",70=>"ConsumeCallI8LocalReturn",71=>"ConsumePresentCallI8LocalReturn",72=>"ConsumeIndexI8LocalReturn",73=>"ConsumeCallIndexI8LocalReturn",74=>"ConsumeHighI8LocalReturn",75=>"ConsumeCallIndexNormalI8LocalReturn",76=>"ConsumeI8LocalPhi",_=>"ConsumeCallI8LocalPhi"};
        }
        if(expression>=78) {
            entryName=expression==78?nameof(NullableValueFixture.ReadWideHelper):nameof(NullableValueFixture.ReadHighWideHelper);
            scalarEntry=expression==78?nameof(NullableValueFixture.ScalarWideHelper):nameof(NullableValueFixture.ScalarHighWideHelper);
            getter=expression==78?"ConsumeWideHelper":"ConsumeHighWideHelper";
        }
        if(expression>=80) {
            entryName=expression==80?nameof(NullableValueFixture.ReadWideBinaryHelper):nameof(NullableValueFixture.ReadHighWideBinaryHelper);
            scalarEntry=expression==80?nameof(NullableValueFixture.ScalarWideBinaryHelper):nameof(NullableValueFixture.ScalarHighWideBinaryHelper);
            getter=expression==80?"ConsumeWideBinaryHelper":"ConsumeHighWideBinaryHelper";
        }
        if(expression>=82) {
            entryName=expression==82?nameof(NullableValueFixture.ReadWideMethodHelper):nameof(NullableValueFixture.ReadHighWideMethodHelper);
            scalarEntry=expression==82?nameof(NullableValueFixture.ScalarWideBinaryHelper):nameof(NullableValueFixture.ScalarHighWideMethodHelper);
            getter=expression==82?"ConsumeWideMethodHelper":"ConsumeHighWideMethodHelper";
        }
        if(expression>=84) {
            entryName=expression switch {84=>nameof(NullableValueFixture.ReadWideOwnerHelper),85=>nameof(NullableValueFixture.ReadHighWideOwnerHelper),86=>nameof(NullableValueFixture.ReadWideCombinedHelper),_=>nameof(NullableValueFixture.ReadHighWideCombinedHelper)};
            scalarEntry=(expression&1)==0?nameof(NullableValueFixture.ScalarWideBinaryHelper):nameof(NullableValueFixture.ScalarHighWideMethodHelper);
            getter=expression switch {84=>"ConsumeWideOwnerHelper",85=>"ConsumeHighWideOwnerHelper",86=>"ConsumeWideCombinedHelper",_=>"ConsumeHighWideCombinedHelper"};
        }
        if(expression>=88) {
            entryName=expression switch {88=>nameof(NullableValueFixture.ReadWideMethodNext),89=>nameof(NullableValueFixture.ReadHighWideMethodNext),90=>nameof(NullableValueFixture.ReadWideOwnerNext),91=>nameof(NullableValueFixture.ReadHighWideOwnerNext),92=>nameof(NullableValueFixture.ReadWideCombinedNext),_=>nameof(NullableValueFixture.ReadHighWideCombinedNext)};
            scalarEntry=(expression&1)==0?nameof(NullableValueFixture.ScalarWideBinaryHelper):nameof(NullableValueFixture.ScalarHighWideMethodHelper);
            getter=expression switch {88=>"ConsumeWideMethodNext",89=>"ConsumeHighWideMethodNext",90=>"ConsumeWideOwnerNext",91=>"ConsumeHighWideOwnerNext",92=>"ConsumeWideCombinedNext",_=>"ConsumeHighWideCombinedNext"};
        }
        if(expression>=94) {
            entryName=expression switch {94=>nameof(NullableValueFixture.ReadMethodChoose),95=>nameof(NullableValueFixture.ReadHighMethodChoose),96=>nameof(NullableValueFixture.ReadOwnerChoose),97=>nameof(NullableValueFixture.ReadHighOwnerChoose),98=>nameof(NullableValueFixture.ReadCombinedChoose),99=>nameof(NullableValueFixture.ReadHighCombinedChoose),100=>nameof(NullableValueFixture.ReadMixedAdvance),_=>nameof(NullableValueFixture.ReadHighMixedAdvance)};
            scalarEntry=expression>=100?((expression&1)==0?nameof(NullableValueFixture.ScalarWideBinaryHelper):nameof(NullableValueFixture.ScalarHighMixedAdvance)):((expression&1)==0?nameof(NullableValueFixture.ScalarChoose):nameof(NullableValueFixture.ScalarHighChoose));
            getter=expression switch {94=>"ConsumeMethodChoose",95=>"ConsumeHighMethodChoose",96=>"ConsumeOwnerChoose",97=>"ConsumeHighOwnerChoose",98=>"ConsumeCombinedChoose",99=>"ConsumeHighCombinedChoose",100=>"ConsumeMixedAdvance",_=>"ConsumeHighMixedAdvance"};
        }
        if(expression>=102) {
            entryName=expression==102?nameof(NullableValueFixture.ReadCountedAdvance):nameof(NullableValueFixture.ReadHighCountedAdvance);
            scalarEntry=expression==102?nameof(NullableValueFixture.ScalarCountedAdvance):nameof(NullableValueFixture.ScalarHighCountedAdvance);
            getter=expression==102?"ConsumeCountedAdvance":"ConsumeHighCountedAdvance";
        }
        if(expression>=104) {
            entryName=expression==104?nameof(NullableValueFixture.ReadBoundedAdvance):nameof(NullableValueFixture.ReadHighBoundedAdvance);
            scalarEntry=expression==104?nameof(NullableValueFixture.ScalarCountedAdvance):nameof(NullableValueFixture.ScalarHighCountedAdvance);
            getter=expression==104?"ConsumeBoundedAdvance":"ConsumeHighBoundedAdvance";
        }
        if(expression>=106) {
            entryName=expression==106?nameof(NullableValueFixture.ReadStrideAdvance):nameof(NullableValueFixture.ReadHighStrideAdvance);
            scalarEntry=expression==106?nameof(NullableValueFixture.ScalarCountedAdvance):nameof(NullableValueFixture.ScalarHighCountedAdvance);
            getter=expression==106?"ConsumeStrideAdvance":"ConsumeHighStrideAdvance";
        }
        if(expression>=108) {
            entryName=expression==108?nameof(NullableValueFixture.ReadRoundedAdvance):nameof(NullableValueFixture.ReadHighRoundedAdvance);
            scalarEntry=expression==108?nameof(NullableValueFixture.ScalarCountedAdvance):nameof(NullableValueFixture.ScalarHighCountedAdvance);
            getter=expression==108?"ConsumeRoundedAdvance":"ConsumeHighRoundedAdvance";
        }
        if(expression>=110) {
            entryName=expression==110?nameof(NullableValueFixture.ReadSeedAdvance):expression==111?nameof(NullableValueFixture.ReadHighSeedAdvance):expression==112?nameof(NullableValueFixture.ReadNegativeSeedAdvance):nameof(NullableValueFixture.ReadHighNegativeSeedAdvance);
            scalarEntry=expression==110?nameof(NullableValueFixture.ScalarCountedAdvance):expression==111?nameof(NullableValueFixture.ScalarHighCountedAdvance):expression==112?nameof(NullableValueFixture.ScalarNegativeSeedAdvance):nameof(NullableValueFixture.ScalarHighNegativeSeedAdvance);
            getter=expression==110?"ConsumeSeedAdvance":expression==111?"ConsumeHighSeedAdvance":expression==112?"ConsumeNegativeSeedAdvance":"ConsumeHighNegativeSeedAdvance";
        }
        if(expression>=114) {
            entryName=expression==114?nameof(NullableValueFixture.ReadDescendingAdvance):expression==115?nameof(NullableValueFixture.ReadHighDescendingAdvance):expression==116?nameof(NullableValueFixture.ReadDescendingRoundedAdvance):nameof(NullableValueFixture.ReadHighDescendingRoundedAdvance);
            scalarEntry=expression==114?nameof(NullableValueFixture.ScalarCountedAdvance):expression==115?nameof(NullableValueFixture.ScalarHighCountedAdvance):expression==116?nameof(NullableValueFixture.ScalarNegativeSeedAdvance):nameof(NullableValueFixture.ScalarHighNegativeSeedAdvance);
            getter=expression==114?"ConsumeDescendingAdvance":expression==115?"ConsumeHighDescendingAdvance":expression==116?"ConsumeDescendingRoundedAdvance":"ConsumeHighDescendingRoundedAdvance";
        }
        if(expression>=118) {
            entryName=expression==118?nameof(NullableValueFixture.ReadInclusiveAdvance):expression==119?nameof(NullableValueFixture.ReadHighInclusiveAdvance):expression==120?nameof(NullableValueFixture.ReadInclusiveAscendingAdvance):nameof(NullableValueFixture.ReadHighInclusiveAscendingAdvance);
            scalarEntry=(expression&1)==0?nameof(NullableValueFixture.ScalarNegativeSeedAdvance):nameof(NullableValueFixture.ScalarHighNegativeSeedAdvance);
            getter=expression==118?"ConsumeInclusiveAdvance":expression==119?"ConsumeHighInclusiveAdvance":expression==120?"ConsumeInclusiveAscendingAdvance":"ConsumeHighInclusiveAscendingAdvance";
        }
        if(expression>=122) {
            entryName=expression==122?nameof(NullableValueFixture.ReadUnsignedAdvance):expression==123?nameof(NullableValueFixture.ReadHighUnsignedAdvance):expression==124?nameof(NullableValueFixture.ReadUnsignedHighCounterAdvance):nameof(NullableValueFixture.ReadHighUnsignedHighCounterAdvance);
            scalarEntry=(expression&1)==0?nameof(NullableValueFixture.ScalarNegativeSeedAdvance):nameof(NullableValueFixture.ScalarHighNegativeSeedAdvance);
            getter=expression==122?"ConsumeUnsignedAdvance":expression==123?"ConsumeHighUnsignedAdvance":expression==124?"ConsumeUnsignedHighCounterAdvance":"ConsumeHighUnsignedHighCounterAdvance";
        }
        if(expression>=126) {
            entryName=expression==126?nameof(NullableValueFixture.ReadUnsigned64Advance):expression==127?nameof(NullableValueFixture.ReadHighUnsigned64Advance):expression==128?nameof(NullableValueFixture.ReadUnsigned64HighCounterAdvance):nameof(NullableValueFixture.ReadHighUnsigned64HighCounterAdvance);
            scalarEntry=(expression&1)==0?nameof(NullableValueFixture.ScalarNegativeSeedAdvance):nameof(NullableValueFixture.ScalarHighNegativeSeedAdvance);
            getter=expression==126?"ConsumeUnsigned64Advance":expression==127?"ConsumeHighUnsigned64Advance":expression==128?"ConsumeUnsigned64HighCounterAdvance":"ConsumeHighUnsigned64HighCounterAdvance";
        }
        if(expression>=130) {
            entryName=expression==130?nameof(NullableValueFixture.ReadSigned64Advance):expression==131?nameof(NullableValueFixture.ReadHighSigned64Advance):expression==132?nameof(NullableValueFixture.ReadSigned64NegativeAdvance):expression==133?nameof(NullableValueFixture.ReadHighSigned64NegativeAdvance):expression==134?nameof(NullableValueFixture.ReadSigned64BoundAdvance):nameof(NullableValueFixture.ReadHighSigned64BoundAdvance);
            scalarEntry=(expression&1)==0?nameof(NullableValueFixture.ScalarNegativeSeedAdvance):nameof(NullableValueFixture.ScalarHighNegativeSeedAdvance);
            getter=expression==130?"ConsumeSigned64Advance":expression==131?"ConsumeHighSigned64Advance":expression==132?"ConsumeSigned64NegativeAdvance":expression==133?"ConsumeHighSigned64NegativeAdvance":expression==134?"ConsumeSigned64BoundAdvance":"ConsumeHighSigned64BoundAdvance";
        }
        if(expression>=136) {
            entryName=expression==136?nameof(NullableValueFixture.ReadSequentialAdvance):expression==137?nameof(NullableValueFixture.ReadHighSequentialAdvance):expression==138?nameof(NullableValueFixture.ReadSequentialOrderedAdvance):nameof(NullableValueFixture.ReadHighSequentialOrderedAdvance);
            scalarEntry=expression==136?nameof(NullableValueFixture.ScalarSequentialAdvance):expression==137?nameof(NullableValueFixture.ScalarHighSequentialAdvance):expression==138?nameof(NullableValueFixture.ScalarSequentialOrderedAdvance):nameof(NullableValueFixture.ScalarHighSequentialOrderedAdvance);
            getter=expression==136?"ConsumeSequentialAdvance":expression==137?"ConsumeHighSequentialAdvance":expression==138?"ConsumeSequentialOrderedAdvance":"ConsumeHighSequentialOrderedAdvance";
        }
        if(expression>=140) {
            entryName=expression==140?nameof(NullableValueFixture.ReadNestedAdvance):expression==141?nameof(NullableValueFixture.ReadHighNestedAdvance):expression==142?nameof(NullableValueFixture.ReadNestedOrderedAdvance):nameof(NullableValueFixture.ReadHighNestedOrderedAdvance);
            scalarEntry=expression==140?nameof(NullableValueFixture.ScalarSequentialAdvance):expression==141?nameof(NullableValueFixture.ScalarHighSequentialAdvance):expression==142?nameof(NullableValueFixture.ScalarNestedOrderedAdvance):nameof(NullableValueFixture.ScalarHighNestedOrderedAdvance);
            getter=expression==140?"ConsumeNestedAdvance":expression==141?"ConsumeHighNestedAdvance":expression==142?"ConsumeNestedOrderedAdvance":"ConsumeHighNestedOrderedAdvance";
        }
        if(expression>=144) {
            entryName=expression==144?nameof(NullableValueFixture.ReadDependentAdvance):expression==145?nameof(NullableValueFixture.ReadHighDependentAdvance):expression==146?nameof(NullableValueFixture.ReadDependentOrderedAdvance):nameof(NullableValueFixture.ReadHighDependentOrderedAdvance);
            scalarEntry=expression==144?nameof(NullableValueFixture.ScalarDependentAdvance):expression==145?nameof(NullableValueFixture.ScalarHighDependentAdvance):expression==146?nameof(NullableValueFixture.ScalarDependentOrderedAdvance):nameof(NullableValueFixture.ScalarHighDependentOrderedAdvance);
            getter=expression==144?"ConsumeDependentAdvance":expression==145?"ConsumeHighDependentAdvance":expression==146?"ConsumeDependentOrderedAdvance":"ConsumeHighDependentOrderedAdvance";
        }
        if(expression>=148) {
            entryName=expression==148?nameof(NullableValueFixture.ReadAffineAdvance):expression==149?nameof(NullableValueFixture.ReadHighAffineAdvance):expression==150?nameof(NullableValueFixture.ReadAffineOrderedAdvance):nameof(NullableValueFixture.ReadHighAffineOrderedAdvance);
            scalarEntry=expression==148?nameof(NullableValueFixture.ScalarAffineAdvance):expression==149?nameof(NullableValueFixture.ScalarHighAffineAdvance):expression==150?nameof(NullableValueFixture.ScalarAffineOrderedAdvance):nameof(NullableValueFixture.ScalarHighAffineOrderedAdvance);
            getter=expression==148?"ConsumeAffineAdvance":expression==149?"ConsumeHighAffineAdvance":expression==150?"ConsumeAffineOrderedAdvance":"ConsumeHighAffineOrderedAdvance";
        }
        if(expression>=152) {
            entryName=expression==152?nameof(NullableValueFixture.ReadScaledAdvance):expression==153?nameof(NullableValueFixture.ReadHighScaledAdvance):expression==154?nameof(NullableValueFixture.ReadScaledOrderedAdvance):nameof(NullableValueFixture.ReadHighScaledOrderedAdvance);
            scalarEntry=expression==152?nameof(NullableValueFixture.ScalarScaledAdvance):expression==153?nameof(NullableValueFixture.ScalarHighScaledAdvance):expression==154?nameof(NullableValueFixture.ScalarScaledOrderedAdvance):nameof(NullableValueFixture.ScalarHighScaledOrderedAdvance);
            getter=expression==152?"ConsumeScaledAdvance":expression==153?"ConsumeHighScaledAdvance":expression==154?"ConsumeScaledOrderedAdvance":"ConsumeHighScaledOrderedAdvance";
        }
        ScalarControlFlowV2LinkedProgramV1 actual =Compile(entryName);
        ScalarControlFlowV2LinkedProgramV1 scalar =Compile(scalarEntry);
        if(expression==134) {
            string identity=actual.MethodObjects.Single(m=>m.MethodIdentity.Contains(".AdvanceSigned64Bound(",StringComparison.Ordinal)).MethodIdentity;
            HybridCpuStaticLinkArtifactV1 image=actual.LinkedImage!;
            HybridCpuLinkedSymbolV1 entry=image.Symbols.Single(s=>s.Name==identity);
            foreach(long payload in new[]{long.MinValue,long.MaxValue,-1L,0L,1L,1L<<40})foreach(long limit in new[]{long.MinValue,-1L,0L,1L,3L,7L}) {
                ulong result=NativeConstantSmoke.Evaluate(image.ImageBytes,unchecked((ulong)payload),unchecked((ulong)limit),entryOffset:checked((int)(entry.Address-image.ImageBase)),imageBase:image.ImageBase);
                if(result!=unchecked((ulong)(payload+Math.Max(0L,limit))))throw new Exception("I8 bound parameter serialized zero/positive-trip semantics");
            }
        }
        if(expression==104) {
            string identity=actual.MethodObjects.Single(m=>m.MethodIdentity.Contains(".AdvanceBounded(",StringComparison.Ordinal)).MethodIdentity;
            HybridCpuStaticLinkArtifactV1 image=actual.LinkedImage!;
            HybridCpuLinkedSymbolV1 entry=image.Symbols.Single(s=>s.Name==identity);
            foreach(long payload in new[]{long.MinValue,long.MaxValue,-1L,0L,1L,1L<<40})foreach(int limit in new[]{int.MinValue,-1,0,1,2,7}) {
                ulong result=NativeConstantSmoke.Evaluate(image.ImageBytes,unchecked((ulong)payload),unchecked((ulong)(long)limit),entryOffset:checked((int)(entry.Address-image.ImageBase)),imageBase:image.ImageBase);
                if(result!=unchecked((ulong)(payload+Math.Max(0,limit))))throw new Exception("Bound parameter serialized zero/positive-trip I8 semantics");
            }
        }
        foreach(int value in new[]{int.MinValue,-1,0,1,int.MaxValue})foreach(bool present in new[]{false,true})foreach(int fallback in expression>0?new[]{int.MinValue,-7,-1,0,1,2,3,9,10,int.MaxValue}:new[]{-7}) {
            ulong packed=present?((ulong)(uint)value<<1)|1UL:0;
            int absent=expression==3?(fallback ^ -7)&255:expression>0?unchecked(fallback+1):generic||owner||combined?-7:-6;
            if(expression is 4 or 5 or 9 or 11 or 13 or 15)absent=fallback==0?-7:unchecked(fallback+1);
            if(expression==6)absent=fallback>0&&fallback<10?fallback:-7;
            if(expression==7)absent=fallback>=0?unchecked(fallback+1):1;
            ulong expected=unchecked((ulong)(long)(present?value:absent));
            if(present && expression>=16)expected=unchecked((ulong)(long)(expression is 16 or 18 or 20?unchecked(value+fallback):(value^fallback)&255));
            if(present && expression>=22)expected=unchecked((ulong)(long)unchecked(value+value));
            if(expression>=25) {
                int result=present?unchecked(2*unchecked(value+(fallback>0?fallback:0))):unchecked(fallback+1+(expression==27&&fallback>0?1:0));
                expected=unchecked((ulong)(long)result);
            }
            if(expression>=28)expected=unchecked((ulong)(long)(present?unchecked(2*unchecked(value+(expression<=29?3:expression==30?5:0)*fallback)):unchecked(fallback+1)));
            if(expression>=32)expected=unchecked((ulong)(long)(present?unchecked(2*unchecked(value+3)):unchecked(fallback+1)));
            if(expression>=34)expected=unchecked((ulong)(long)(present?unchecked(2*unchecked(value+(expression==36?0:expression==37?5:3)*unchecked(value+(expression>=38?1:0)))):unchecked(fallback+1)));
            if(expression>=40)expected=unchecked((ulong)(long)(present?fallback>0?unchecked(8*value+(expression is 42 or 43 or 45?6:0)):unchecked(2*(expression>=44?value^fallback:value)):unchecked(fallback+1)));
            int iterations=expression>=50?Math.Clamp(fallback,0,3):fallback>0?0:3;
            if(expression>=46)expected=unchecked((ulong)(long)(present?unchecked(2*unchecked(value+iterations*unchecked(value+(expression is 48 or 49 or 51?1:0)))):unchecked(fallback+1)));
            if(expression>=52) {
                iterations=expression>=56?Math.Clamp(fallback,0,3):fallback>0?0:3;
                int payload=unchecked(value+iterations*unchecked(value+(expression is 54 or 55 or 57?1:0)));
                expected=unchecked((ulong)(present?(expression>=56?fallback<3:fallback>0)?(long)payload:(long)unchecked(payload+payload):(long)unchecked(fallback+1)));
            }
            if(expression>=58) {
                bool indexed=expression is 62 or 63 or 65 or 67, called=expression is 60 or 61 or 63 or 65 or 67;
                iterations=indexed?Math.Clamp(fallback,0,3):fallback>0?0:3;
                int payload=unchecked(value+iterations*unchecked(value+(called?1:0)));
                bool early=indexed?fallback<3:fallback>0;
                long result=early?expression is 64 or 65?unchecked(((long)payload<<40)^fallback):(long)payload+fallback:expression>=66?(long)payload+fallback:unchecked(payload+payload);
                expected=unchecked((ulong)(present?result:(long)unchecked(fallback+1)));
            }
            if(expression>=68) {
                bool indexed=expression is 72 or 73 or 75, called=expression is 70 or 71 or 73 or 75 or 77, phi=expression>=76;
                iterations=phi?3:indexed?Math.Clamp(fallback,0,3):fallback>0?0:3;
                long payload=(long)value+(long)iterations*(called?unchecked(value+1):value);
                if(phi && fallback<=0)payload=(long)value^fallback;
                bool early=!phi && (indexed?fallback<3:fallback>0);
                long result=early?expression==74?unchecked((payload<<40)^fallback):payload+fallback:expression==75?payload+fallback:unchecked(payload+payload);
                expected=unchecked((ulong)(present?result:(long)unchecked(fallback+1)));
            }
            if(expression>=78) {
                long initial=expression==78?value:unchecked((long)value+long.MaxValue);
                long result=fallback>0?unchecked(initial+fallback):expression==79?unchecked(initial+3):unchecked(2*(initial+3));
                expected=unchecked((ulong)(present?result:unchecked((long)unchecked(fallback+1))));
            }
            if(expression>=80) {
                long initial=expression==80?value:unchecked((long)value+long.MinValue);
                long result=fallback>0?unchecked(initial+fallback):expression==81?unchecked(initial+3L*((long)fallback<<40)):unchecked(2*(initial+3L*fallback));
                expected=unchecked((ulong)(present?result:unchecked((long)unchecked(fallback+1))));
            }
            if(expression>=82) {
                bool high=(expression&1)!=0;
                long initial=!high?value:unchecked((long)value+long.MinValue);
                long step=!high?fallback:(long)fallback<<40;
                long result=fallback>0?unchecked(initial+fallback):high?unchecked(initial+3*step):unchecked(2*(initial+3*step));
                expected=unchecked((ulong)(present?result:unchecked((long)unchecked(fallback+1))));
            }
            if(expression>=94) {
                bool high=(expression&1)!=0;
                long initial=!high?value:unchecked((long)value+long.MinValue);
                long step=expression>=152?9:expression>=148?6:expression>=144?3:expression>=136?4:expression>=116 || expression is 112 or 113?3:expression>=102?2:high && expression<100?(long)fallback<<40:fallback;
                int updates=expression<100?2:3;
                long result=fallback>0?unchecked(initial+fallback):high?unchecked(initial+updates*step):unchecked(2*(initial+updates*step));
                expected=unchecked((ulong)(present?result:unchecked((long)unchecked(fallback+1))));
            }
            if(expression is 138 or 139 || expression is 142 or 143 or 146 or 147 or 150 or 151 or 154 or 155) {
                bool high=(expression&1)!=0;
                long initial=high?unchecked((long)value+long.MinValue):value;
                long after=fallback>0?unchecked(initial+fallback):expression>=154?unchecked(7625597484987L*initial+3812798742493L):expression>=150?unchecked(387420489L*initial+193710244L):expression>=146?unchecked(19683L*initial+9841L):expression>=142?unchecked(531441L*initial+265720L):unchecked(27L*initial+104L);
                expected=unchecked((ulong)(present?(fallback>0||high?after:2*after):(long)unchecked(fallback+1)));
            }
            foreach((ScalarControlFlowV2LinkedProgramV1? program, string? name) in new[]{(actual,getter),(scalar,scalarEntry)}) {
                string identity =program.MethodObjects.Single(m=>m.MethodIdentity.Contains("."+name+"(",StringComparison.Ordinal)).MethodIdentity;
                HybridCpuStaticLinkArtifactV1 image =program.LinkedImage!; HybridCpuLinkedSymbolV1 entry =image.Symbols.Single(s=>s.Name==identity);
                int calls=0;
                ulong result=NativeConstantSmoke.Evaluate(image.ImageBytes,packed,unchecked((ulong)(long)fallback),entryOffset:checked((int)(entry.Address-image.ImageBase)),imageBase:image.ImageBase,onCall:()=>calls++);
                int expectedCalls=expression>1?0:expression==1||lazy?present?0:1:1;
                if(expression is 5 or 7 or 8 or 9 or 10 or 11 or 12 or 13 or 14 or 15 or 16 or 17 or 18 or 19)expectedCalls=present||(expression is 5 or 9 or 11 or 13 or 15)&&fallback==0?0:1;
                if(expression>=20)expectedCalls=present?0:1;
                if(expression>=32)expectedCalls=present?(name==getter?3:0):1;
                if(expression>=34)expectedCalls=present?(expression>=38 && name==getter?3:0):1;
                if(expression>=40)expectedCalls=present?(expression is 42 or 43 or 45 && fallback>0 && name==getter?3:0):1;
                if(expression>=46)expectedCalls=present?(expression is 48 or 49 or 51 && name==getter?iterations:0):1;
                if(expression>=52)expectedCalls=present?(expression is 54 or 55 or 57 && name==getter?iterations:0):1;
                if(expression>=58)expectedCalls=present?(expression is 60 or 61 or 63 or 65 or 67 && name==getter?iterations:0):1;
                if(expression>=68)expectedCalls=present?(expression is 70 or 71 or 73 or 75 or 77 && (expression<76 || fallback>0) && name==getter?iterations:0):1;
                if(expression>=78)expectedCalls=present?(fallback<=0 && name==getter?3:0):1;
                if(result!=expected || calls!=expectedCalls)throw new Exception($"Nullable helper physical ABI/scalar oracle expression={expression} method={name} value={value} present={present} fallback={fallback} actual={result:X16} expected={expected:X16} calls={calls}/{expectedCalls}");
            }
        }
        Console.WriteLine("PASS selected external "+(expression>0?"lazy dynamic expression "+expression:lazy?"lazy coalescing":combined?"combined VAR/MVAR":owner?"constructed owner":generic?"closed generic":"binary I4")+" helper nullable default linked serialized words/physical ABI, exact present/null call count, independent scalar boundaries");
    }
    private static void RunHelperDefaultWords(bool external = false, bool constant = false)
    {
        byte[] pe=File.ReadAllBytes(typeof(NullableValueFixture).Assembly.Location);
        ScalarControlFlowV2LinkedProgramV1 Compile(string entry) {
            ManagedCallGraphCompilationV1 graph =new RestrictedCilImporterV1(mode:RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(
                ManagedBodyWorldModeV1.StandaloneRestrictedModule,pe,"nullable-helper-default",[new(typeof(NullableValueFixture).FullName!,entry)],[],
                DependencyModules:external?[new(File.ReadAllBytes(typeof(ManagedPortSmoke.Dependency.MetadataOnlyCallee).Assembly.Location),"selected-fallback")]:[],
                MetadataOnlyModules:[new(File.ReadAllBytes(typeof(object).Assembly.Location),"core")]));
            if(graph.Status!=RestrictedCilImportStatusV1.Success)throw new Exception(string.Join(';',graph.Diagnostics));
            ScalarControlFlowV2LinkedProgramV1 linked =new ScalarControlFlowV2ObjectLinkerV1().Link(graph,graph.Graph!.RootIdentities.Single());
            if(linked.Status!=ScalarControlFlowV2LinkStatusV1.Success)throw new Exception(string.Join(';',linked.Diagnostics));
            return linked;
        }
        ScalarControlFlowV2LinkedProgramV1 actual =Compile(constant?nameof(NullableValueFixture.ReadConstantHelper):external?nameof(NullableValueFixture.ReadExternalHelper):nameof(NullableValueFixture.ReadHelper)); ScalarControlFlowV2LinkedProgramV1 scalar =Compile(constant?nameof(NullableValueFixture.ReadConstantHelperScalar):nameof(NullableValueFixture.ReadHelperScalar));
        ulong Evaluate(ScalarControlFlowV2LinkedProgramV1 program,string method,ulong input,ulong fallback) {
            string identity =program.MethodObjects.Single(m=>m.MethodIdentity.Contains("."+method+"(",StringComparison.Ordinal)).MethodIdentity;
            HybridCpuStaticLinkArtifactV1 image =program.LinkedImage!; HybridCpuLinkedSymbolV1 entry =image.Symbols.Single(s=>s.Name==identity);
            try {
                int calls=0;
                ulong result=NativeConstantSmoke.Evaluate(image.ImageBytes,input,fallback,checked((int)(entry.Address-image.ImageBase)),image.ImageBase,onCall:()=>calls++);
                int expectedCalls=method.StartsWith("Consume",StringComparison.Ordinal)||method.EndsWith("Scalar",StringComparison.Ordinal)?1:2;
                if(calls!=expectedCalls)throw new Exception("Nullable default must eagerly execute its exact helper, including a present payload");
                return result;
            }
            catch {
                Console.WriteLine("HELPER-ENTRY "+method+" "+entry.Address+" base="+image.ImageBase+" "+string.Join(';',image.Symbols.Select(s=>s.Name+":"+s.Address)));
                foreach(HybridCpuLinkedSectionV1? section in image.Sections.Where(s=>s.Name==".text"))for(ulong at=section.Address;at<section.Address+section.Size;at+=(ulong)HybridCpuBundleSerializer.BundleSizeBytes) {
                    HybridCpuInstructionBundle bundle =new HybridCpuInstructionBundle();bundle.TryReadBytes(image.ImageBytes,checked((int)(at-image.ImageBase)));
                    for(int slot=0;slot<8;slot++) {
                        HybridCpuInstructionWord word =bundle.GetInstruction(slot);if((HybridCpuOpcode)word.OpCode==HybridCpuOpcode.Nope)continue;
                        HybridCpuInstructionWord.TryUnpackArchRegs(word.Word1,out byte rd,out byte rs1,out byte rs2);
                        Console.WriteLine($"HELPER-WORD {at-image.ImageBase}:{slot} {(HybridCpuOpcode)word.OpCode} x{rd},x{rs1},x{rs2} {(short)word.Immediate}");
                    }
                }
                throw;
            }
        }
        if(actual.MethodObjects.Single(m=>m.MethodIdentity.Contains(external?".AdjustFallback(":".AdjustHelper(",StringComparison.Ordinal)).CodeSha256!=
            scalar.MethodObjects.Single(m=>m.MethodIdentity.Contains(".AdjustHelperScalar(",StringComparison.Ordinal)).CodeSha256)throw new Exception("Nullable helper default independent scalar leaf bytes");
        foreach(int input in new[]{int.MinValue,-1,0,1,int.MaxValue})foreach(int fallback in constant?new[]{-7}:new[]{int.MinValue,-7,0,int.MaxValue}) {
            foreach(bool present in new[]{false,true}) {
                ulong packed=present?((ulong)(uint)input<<1)|1UL:0;
                ulong expected=unchecked((ulong)(long)(present?input:unchecked(fallback+1)));
                if(Evaluate(actual,constant?"ConsumeConstantHelper":external?"ConsumeExternalHelper":"ConsumeHelper",packed,unchecked((ulong)fallback))!=expected||
                    Evaluate(scalar,constant?nameof(NullableValueFixture.ReadConstantHelperScalar):nameof(NullableValueFixture.ReadHelperScalar),packed,unchecked((ulong)fallback))!=expected)throw new Exception("Nullable helper linked scalar/payload/ABI words");
            }
            long callerExpected=input<0?unchecked(fallback+1):input;
            if(Evaluate(actual,constant?nameof(NullableValueFixture.ReadConstantHelper):external?nameof(NullableValueFixture.ReadExternalHelper):nameof(NullableValueFixture.ReadHelper),unchecked((ulong)input),unchecked((ulong)fallback))!=unchecked((ulong)callerExpected)||
                NullableValueFixture.ReadHelper(input,fallback)!=callerExpected)throw new Exception("Nullable helper linked caller/host words");
        }
        Console.WriteLine("PASS Nullable<int> "+(constant?"constant ":"")+(external?"selected external ":"")+"helper default linked call/return words, packed carrier preservation, scalar leaf bytes and independent caller/helper/CoreCLR boundaries");
    }
    private static void RunParameterWords(bool presence = false, bool explicitDefault = false, bool dynamicDefault = false, bool computedDefault = false, bool computedConditional = false)
    {
        computedDefault |= computedConditional;
        dynamicDefault |= computedDefault;
        byte[] pe=File.ReadAllBytes(typeof(NullableValueFixture).Assembly.Location);
        ManagedCallGraphCompilationV1 graph =new RestrictedCilImporterV1(mode:RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(
            ManagedBodyWorldModeV1.StandaloneRestrictedModule,pe,"nullable-parameter-words",[new(typeof(NullableValueFixture).FullName!,computedConditional?nameof(NullableValueFixture.ReadComputedConditional):computedDefault?nameof(NullableValueFixture.ReadComputed):dynamicDefault?nameof(NullableValueFixture.ReadDynamic):presence?nameof(NullableValueFixture.ReadPresence):explicitDefault?nameof(NullableValueFixture.ReadDefault):nameof(NullableValueFixture.ReadParameter))],[],
            MetadataOnlyModules:[new(File.ReadAllBytes(typeof(object).Assembly.Location),"core")]));
        if(graph.Status!=RestrictedCilImportStatusV1.Success)throw new Exception(string.Join(';',graph.Diagnostics));
        ManagedCompiledMethodV1 helper =graph.Methods.Single(m=>m.Identity.MethodName==(computedConditional?"ConsumeComputedConditional":computedDefault?"ConsumeComputed":dynamicDefault?"ConsumeDynamic":presence?"ConsumePresence":explicitDefault?"ConsumeDefault":"ConsumeParameter"));
        ScalarControlFlowV2MethodObjectV1 obj =(ScalarControlFlowV2MethodObjectV1)typeof(ScalarControlFlowV2ObjectLinkerV1).GetMethod("CompileMethod",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!.Invoke(null,[helper,0])!;
        if(obj.ObjectArtifact.Status!=HybridCpuObjectStatusV1.Success)throw new Exception("Nullable parameter HCO");
        byte[] code=obj.ObjectArtifact.Sections.Single(s=>s.Name==".text").Data;
        byte[]? scalarCode=null;
        if(presence || explicitDefault || dynamicDefault) {
            ManagedCallGraphCompilationV1 scalarGraph =new RestrictedCilImporterV1(mode:RestrictedCilImportModeV1.ScalarControlFlowV2).ImportBodyWorld(new(
                ManagedBodyWorldModeV1.StandaloneRestrictedModule,pe,"nullable-parameter-scalar",[new(typeof(NullableValueFixture).FullName!,computedConditional?nameof(NullableValueFixture.PackedComputedConditional):computedDefault?nameof(NullableValueFixture.PackedComputed):dynamicDefault?nameof(NullableValueFixture.PackedDynamic):explicitDefault?nameof(NullableValueFixture.PackedDefault):nameof(NullableValueFixture.PackedPresence))],[]));
            if(scalarGraph.Status!=RestrictedCilImportStatusV1.Success)throw new Exception("Nullable presence scalar import");
            ScalarControlFlowV2MethodObjectV1 scalarObject =(ScalarControlFlowV2MethodObjectV1)typeof(ScalarControlFlowV2ObjectLinkerV1).GetMethod("CompileMethod",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!.Invoke(null,[scalarGraph.Methods.Single(),0])!;
            if(scalarObject.ObjectArtifact.Status!=HybridCpuObjectStatusV1.Success)throw new Exception("Nullable presence scalar object");
            scalarCode=scalarObject.ObjectArtifact.Sections.Single(s=>s.Name==".text").Data;
        }
        foreach(int input in new[]{int.MinValue,-1,0,1,int.MaxValue})foreach(bool present in new[]{false,true})foreach(int fallback in dynamicDefault?new[]{int.MinValue,-7,0,int.MaxValue}:new[]{0}) {
            ulong packed=present?((ulong)(uint)input<<1)|1UL:0;
            long expected=presence?(present?1L:0L):present?input:computedConditional&&fallback==0?-7L:computedDefault?unchecked(fallback+1):dynamicDefault?fallback:explicitDefault?-7L:0L;
            if(NativeConstantSmoke.Evaluate(code,packed,unchecked((ulong)fallback))!=unchecked((ulong)expected)||
                scalarCode is not null && NativeConstantSmoke.Evaluate(scalarCode,packed,unchecked((ulong)fallback))!=unchecked((ulong)expected) ||
                (computedConditional?NullableValueFixture.PackedComputedConditional(unchecked((long)packed),fallback):computedDefault?NullableValueFixture.PackedComputed(unchecked((long)packed),fallback):dynamicDefault?NullableValueFixture.PackedDynamic(unchecked((long)packed),fallback):presence?NullableValueFixture.PackedPresence(unchecked((long)packed)):explicitDefault?NullableValueFixture.PackedDefault(unchecked((long)packed)):NullableValueFixture.PackedParameter(unchecked((long)packed)))!=expected ||
                (computedConditional?NullableValueFixture.ReadComputedConditional(input,fallback):computedDefault?NullableValueFixture.ReadComputed(input,fallback):dynamicDefault?NullableValueFixture.ReadDynamic(input,fallback):presence?NullableValueFixture.ReadPresence(input):explicitDefault?NullableValueFixture.ReadDefault(input):NullableValueFixture.ReadParameter(input))!=(presence?1L:(long)input))
                throw new Exception("Nullable parameter packed ABI/CoreCLR/scalar/serialized words");
        }
        ScalarControlFlowV2LinkedProgramV1 linked =new ScalarControlFlowV2ObjectLinkerV1().Link(graph,graph.Graph!.RootIdentities.Single());
        if(linked.Status!=ScalarControlFlowV2LinkStatusV1.Success)throw new Exception("Nullable parameter caller/helper link");
        Console.WriteLine("PASS private Nullable<int> parameter "+(computedConditional?"computed conditional default":computedDefault?"computed default":dynamicDefault?"dynamic default":presence?"presence":explicitDefault?"constant default":"value")+" packed I8 ABI -> encoded helper words, independent scalar/CoreCLR boundaries and caller/helper image");
    }
}

public static class NullableValueFixture
{
    private static class StructZero3Owner<O> where O:struct {
        private static U Adjust<U>(U p0,U p1,U p2) where U:struct=>default(U);
        private static U Relay<U>(U p0,U p1,U p2) where U:struct=>Adjust<U>(p0,p1,p2);
        private static int? StructZeroCopy3I4(int? value,int fallback)=>new int?(value.GetValueOrDefault(Adjust<int>(fallback,unchecked(fallback+1),unchecked(fallback+2))));
        public static long UseI4(int input,int delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructZeroCopy3I4(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
        private static int? StructZeroCopy3I4Transitive(int? value,int fallback)=>new int?(value.GetValueOrDefault(Relay<int>(fallback,unchecked(fallback+1),unchecked(fallback+2))));
        public static long UseI4Transitive(int input,int delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructZeroCopy3I4Transitive(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
        private static int? StructZeroCopy3I8(int? value,long fallback)=>new int?(value.GetValueOrDefault(unchecked((int)Adjust<long>(fallback,unchecked(fallback+1),unchecked(fallback+2)))));
        public static long UseI8(int input,long delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructZeroCopy3I8(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
        private static int? StructZeroCopy3I8Transitive(int? value,long fallback)=>new int?(value.GetValueOrDefault(unchecked((int)Relay<long>(fallback,unchecked(fallback+1),unchecked(fallback+2)))));
        public static long UseI8Transitive(int input,long delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructZeroCopy3I8Transitive(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
    }
    public static long ReadStructZero3I4OwnerI4(int input,int delta)=>StructZero3Owner<int>.UseI4(input,delta);
    public static long ReadStructZero3I4OwnerI4Transitive(int input,int delta)=>StructZero3Owner<int>.UseI4Transitive(input,delta);
    public static long ReadStructZero3I4OwnerI8(int input,int delta)=>StructZero3Owner<long>.UseI4(input,delta);
    public static long ReadStructZero3I4OwnerI8Transitive(int input,int delta)=>StructZero3Owner<long>.UseI4Transitive(input,delta);
    public static long ReadStructZero3I8OwnerI4(int input,long delta)=>StructZero3Owner<int>.UseI8(input,delta);
    public static long ReadStructZero3I8OwnerI4Transitive(int input,long delta)=>StructZero3Owner<int>.UseI8Transitive(input,delta);
    public static long ReadStructZero3I8OwnerI8(int input,long delta)=>StructZero3Owner<long>.UseI8(input,delta);
    public static long ReadStructZero3I8OwnerI8Transitive(int input,long delta)=>StructZero3Owner<long>.UseI8Transitive(input,delta);
    public static long ScalarStructZero3I4(int input,int delta)=>((long)input<<32)^delta^(input<0?0:unchecked(input+(int)delta));
    public static long ScalarStructZero3I8(int input,long delta)=>((long)input<<32)^delta^(input<0?0:unchecked(input+(int)delta));
    private static class StructZero4Owner<O> where O:struct {
        private static U Adjust<U>(U p0,U p1,U p2,U p3) where U:struct=>default(U);
        private static U Relay<U>(U p0,U p1,U p2,U p3) where U:struct=>Adjust<U>(p0,p1,p2,p3);
        private static int? StructZeroCopy4I4(int? value,int fallback)=>new int?(value.GetValueOrDefault(Adjust<int>(fallback,unchecked(fallback+1),unchecked(fallback+2),unchecked(fallback+3))));
        public static long UseI4(int input,int delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructZeroCopy4I4(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
        private static int? StructZeroCopy4I4Transitive(int? value,int fallback)=>new int?(value.GetValueOrDefault(Relay<int>(fallback,unchecked(fallback+1),unchecked(fallback+2),unchecked(fallback+3))));
        public static long UseI4Transitive(int input,int delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructZeroCopy4I4Transitive(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
        private static int? StructZeroCopy4I8(int? value,long fallback)=>new int?(value.GetValueOrDefault(unchecked((int)Adjust<long>(fallback,unchecked(fallback+1),unchecked(fallback+2),unchecked(fallback+3)))));
        public static long UseI8(int input,long delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructZeroCopy4I8(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
        private static int? StructZeroCopy4I8Transitive(int? value,long fallback)=>new int?(value.GetValueOrDefault(unchecked((int)Relay<long>(fallback,unchecked(fallback+1),unchecked(fallback+2),unchecked(fallback+3)))));
        public static long UseI8Transitive(int input,long delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructZeroCopy4I8Transitive(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
    }
    public static long ReadStructZero4I4OwnerI4(int input,int delta)=>StructZero4Owner<int>.UseI4(input,delta);
    public static long ReadStructZero4I4OwnerI4Transitive(int input,int delta)=>StructZero4Owner<int>.UseI4Transitive(input,delta);
    public static long ReadStructZero4I4OwnerI8(int input,int delta)=>StructZero4Owner<long>.UseI4(input,delta);
    public static long ReadStructZero4I4OwnerI8Transitive(int input,int delta)=>StructZero4Owner<long>.UseI4Transitive(input,delta);
    public static long ReadStructZero4I8OwnerI4(int input,long delta)=>StructZero4Owner<int>.UseI8(input,delta);
    public static long ReadStructZero4I8OwnerI4Transitive(int input,long delta)=>StructZero4Owner<int>.UseI8Transitive(input,delta);
    public static long ReadStructZero4I8OwnerI8(int input,long delta)=>StructZero4Owner<long>.UseI8(input,delta);
    public static long ReadStructZero4I8OwnerI8Transitive(int input,long delta)=>StructZero4Owner<long>.UseI8Transitive(input,delta);
    public static long ScalarStructZero4I4(int input,int delta)=>((long)input<<32)^delta^(input<0?0:unchecked(input+(int)delta));
    public static long ScalarStructZero4I8(int input,long delta)=>((long)input<<32)^delta^(input<0?0:unchecked(input+(int)delta));
    private static class StructZero5Owner<O> where O:struct {
        private static U Adjust<U>(U p0,U p1,U p2,U p3,U p4) where U:struct=>default(U);
        private static U Relay<U>(U p0,U p1,U p2,U p3,U p4) where U:struct=>Adjust<U>(p0,p1,p2,p3,p4);
        private static int? StructZeroCopy5I4(int? value,int fallback)=>new int?(value.GetValueOrDefault(Adjust<int>(fallback,unchecked(fallback+1),unchecked(fallback+2),unchecked(fallback+3),unchecked(fallback+4))));
        public static long UseI4(int input,int delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructZeroCopy5I4(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
        private static int? StructZeroCopy5I4Transitive(int? value,int fallback)=>new int?(value.GetValueOrDefault(Relay<int>(fallback,unchecked(fallback+1),unchecked(fallback+2),unchecked(fallback+3),unchecked(fallback+4))));
        public static long UseI4Transitive(int input,int delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructZeroCopy5I4Transitive(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
        private static int? StructZeroCopy5I8(int? value,long fallback)=>new int?(value.GetValueOrDefault(unchecked((int)Adjust<long>(fallback,unchecked(fallback+1),unchecked(fallback+2),unchecked(fallback+3),unchecked(fallback+4)))));
        public static long UseI8(int input,long delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructZeroCopy5I8(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
        private static int? StructZeroCopy5I8Transitive(int? value,long fallback)=>new int?(value.GetValueOrDefault(unchecked((int)Relay<long>(fallback,unchecked(fallback+1),unchecked(fallback+2),unchecked(fallback+3),unchecked(fallback+4)))));
        public static long UseI8Transitive(int input,long delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructZeroCopy5I8Transitive(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
    }
    public static long ReadStructZero5I4OwnerI4(int input,int delta)=>StructZero5Owner<int>.UseI4(input,delta);
    public static long ReadStructZero5I4OwnerI4Transitive(int input,int delta)=>StructZero5Owner<int>.UseI4Transitive(input,delta);
    public static long ReadStructZero5I4OwnerI8(int input,int delta)=>StructZero5Owner<long>.UseI4(input,delta);
    public static long ReadStructZero5I4OwnerI8Transitive(int input,int delta)=>StructZero5Owner<long>.UseI4Transitive(input,delta);
    public static long ReadStructZero5I8OwnerI4(int input,long delta)=>StructZero5Owner<int>.UseI8(input,delta);
    public static long ReadStructZero5I8OwnerI4Transitive(int input,long delta)=>StructZero5Owner<int>.UseI8Transitive(input,delta);
    public static long ReadStructZero5I8OwnerI8(int input,long delta)=>StructZero5Owner<long>.UseI8(input,delta);
    public static long ReadStructZero5I8OwnerI8Transitive(int input,long delta)=>StructZero5Owner<long>.UseI8Transitive(input,delta);
    public static long ScalarStructZero5I4(int input,int delta)=>((long)input<<32)^delta^(input<0?0:unchecked(input+(int)delta));
    public static long ScalarStructZero5I8(int input,long delta)=>((long)input<<32)^delta^(input<0?0:unchecked(input+(int)delta));
    private static class StructZero6Owner<O> where O:struct {
        private static U Adjust<U>(U p0,U p1,U p2,U p3,U p4,U p5) where U:struct=>default(U);
        private static U Relay<U>(U p0,U p1,U p2,U p3,U p4,U p5) where U:struct=>Adjust<U>(p0,p1,p2,p3,p4,p5);
        private static int? StructZeroCopy6I4(int? value,int fallback)=>new int?(value.GetValueOrDefault(Adjust<int>(fallback,unchecked(fallback+1),unchecked(fallback+2),unchecked(fallback+3),unchecked(fallback+4),unchecked(fallback+5))));
        public static long UseI4(int input,int delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructZeroCopy6I4(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
        private static int? StructZeroCopy6I4Transitive(int? value,int fallback)=>new int?(value.GetValueOrDefault(Relay<int>(fallback,unchecked(fallback+1),unchecked(fallback+2),unchecked(fallback+3),unchecked(fallback+4),unchecked(fallback+5))));
        public static long UseI4Transitive(int input,int delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructZeroCopy6I4Transitive(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
        private static int? StructZeroCopy6I8(int? value,long fallback)=>new int?(value.GetValueOrDefault(unchecked((int)Adjust<long>(fallback,unchecked(fallback+1),unchecked(fallback+2),unchecked(fallback+3),unchecked(fallback+4),unchecked(fallback+5)))));
        public static long UseI8(int input,long delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructZeroCopy6I8(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
        private static int? StructZeroCopy6I8Transitive(int? value,long fallback)=>new int?(value.GetValueOrDefault(unchecked((int)Relay<long>(fallback,unchecked(fallback+1),unchecked(fallback+2),unchecked(fallback+3),unchecked(fallback+4),unchecked(fallback+5)))));
        public static long UseI8Transitive(int input,long delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructZeroCopy6I8Transitive(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
    }
    public static long ReadStructZero6I4OwnerI4(int input,int delta)=>StructZero6Owner<int>.UseI4(input,delta);
    public static long ReadStructZero6I4OwnerI4Transitive(int input,int delta)=>StructZero6Owner<int>.UseI4Transitive(input,delta);
    public static long ReadStructZero6I4OwnerI8(int input,int delta)=>StructZero6Owner<long>.UseI4(input,delta);
    public static long ReadStructZero6I4OwnerI8Transitive(int input,int delta)=>StructZero6Owner<long>.UseI4Transitive(input,delta);
    public static long ReadStructZero6I8OwnerI4(int input,long delta)=>StructZero6Owner<int>.UseI8(input,delta);
    public static long ReadStructZero6I8OwnerI4Transitive(int input,long delta)=>StructZero6Owner<int>.UseI8Transitive(input,delta);
    public static long ReadStructZero6I8OwnerI8(int input,long delta)=>StructZero6Owner<long>.UseI8(input,delta);
    public static long ReadStructZero6I8OwnerI8Transitive(int input,long delta)=>StructZero6Owner<long>.UseI8Transitive(input,delta);
    public static long ScalarStructZero6I4(int input,int delta)=>((long)input<<32)^delta^(input<0?0:unchecked(input+(int)delta));
    public static long ScalarStructZero6I8(int input,long delta)=>((long)input<<32)^delta^(input<0?0:unchecked(input+(int)delta));
    private static class StructZero7Owner<O> where O:struct {
        private static U Adjust<U>(U p0,U p1,U p2,U p3,U p4,U p5,U p6) where U:struct=>default(U);
        private static U Relay<U>(U p0,U p1,U p2,U p3,U p4,U p5,U p6) where U:struct=>Adjust<U>(p0,p1,p2,p3,p4,p5,p6);
        private static int? StructZeroCopy7I4(int? value,int fallback)=>new int?(value.GetValueOrDefault(Adjust<int>(fallback,unchecked(fallback+1),unchecked(fallback+2),unchecked(fallback+3),unchecked(fallback+4),unchecked(fallback+5),unchecked(fallback+6))));
        public static long UseI4(int input,int delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructZeroCopy7I4(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
        private static int? StructZeroCopy7I4Transitive(int? value,int fallback)=>new int?(value.GetValueOrDefault(Relay<int>(fallback,unchecked(fallback+1),unchecked(fallback+2),unchecked(fallback+3),unchecked(fallback+4),unchecked(fallback+5),unchecked(fallback+6))));
        public static long UseI4Transitive(int input,int delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructZeroCopy7I4Transitive(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
        private static int? StructZeroCopy7I8(int? value,long fallback)=>new int?(value.GetValueOrDefault(unchecked((int)Adjust<long>(fallback,unchecked(fallback+1),unchecked(fallback+2),unchecked(fallback+3),unchecked(fallback+4),unchecked(fallback+5),unchecked(fallback+6)))));
        public static long UseI8(int input,long delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructZeroCopy7I8(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
        private static int? StructZeroCopy7I8Transitive(int? value,long fallback)=>new int?(value.GetValueOrDefault(unchecked((int)Relay<long>(fallback,unchecked(fallback+1),unchecked(fallback+2),unchecked(fallback+3),unchecked(fallback+4),unchecked(fallback+5),unchecked(fallback+6)))));
        public static long UseI8Transitive(int input,long delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructZeroCopy7I8Transitive(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
    }
    public static long ReadStructZero7I4OwnerI4(int input,int delta)=>StructZero7Owner<int>.UseI4(input,delta);
    public static long ReadStructZero7I4OwnerI4Transitive(int input,int delta)=>StructZero7Owner<int>.UseI4Transitive(input,delta);
    public static long ReadStructZero7I4OwnerI8(int input,int delta)=>StructZero7Owner<long>.UseI4(input,delta);
    public static long ReadStructZero7I4OwnerI8Transitive(int input,int delta)=>StructZero7Owner<long>.UseI4Transitive(input,delta);
    public static long ReadStructZero7I8OwnerI4(int input,long delta)=>StructZero7Owner<int>.UseI8(input,delta);
    public static long ReadStructZero7I8OwnerI4Transitive(int input,long delta)=>StructZero7Owner<int>.UseI8Transitive(input,delta);
    public static long ReadStructZero7I8OwnerI8(int input,long delta)=>StructZero7Owner<long>.UseI8(input,delta);
    public static long ReadStructZero7I8OwnerI8Transitive(int input,long delta)=>StructZero7Owner<long>.UseI8Transitive(input,delta);
    public static long ScalarStructZero7I4(int input,int delta)=>((long)input<<32)^delta^(input<0?0:unchecked(input+(int)delta));
    public static long ScalarStructZero7I8(int input,long delta)=>((long)input<<32)^delta^(input<0?0:unchecked(input+(int)delta));
    private static class StructZero8Owner<O> where O:struct {
        private static U Adjust<U>(U p0,U p1,U p2,U p3,U p4,U p5,U p6,U p7) where U:struct=>default(U);
        private static U Relay<U>(U p0,U p1,U p2,U p3,U p4,U p5,U p6,U p7) where U:struct=>Adjust<U>(p0,p1,p2,p3,p4,p5,p6,p7);
        private static int? StructZeroCopy8I4(int? value,int fallback)=>new int?(value.GetValueOrDefault(Adjust<int>(fallback,unchecked(fallback+1),unchecked(fallback+2),unchecked(fallback+3),unchecked(fallback+4),unchecked(fallback+5),unchecked(fallback+6),unchecked(fallback+7))));
        public static long UseI4(int input,int delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructZeroCopy8I4(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
        private static int? StructZeroCopy8I4Transitive(int? value,int fallback)=>new int?(value.GetValueOrDefault(Relay<int>(fallback,unchecked(fallback+1),unchecked(fallback+2),unchecked(fallback+3),unchecked(fallback+4),unchecked(fallback+5),unchecked(fallback+6),unchecked(fallback+7))));
        public static long UseI4Transitive(int input,int delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructZeroCopy8I4Transitive(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
        private static int? StructZeroCopy8I8(int? value,long fallback)=>new int?(value.GetValueOrDefault(unchecked((int)Adjust<long>(fallback,unchecked(fallback+1),unchecked(fallback+2),unchecked(fallback+3),unchecked(fallback+4),unchecked(fallback+5),unchecked(fallback+6),unchecked(fallback+7)))));
        public static long UseI8(int input,long delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructZeroCopy8I8(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
        private static int? StructZeroCopy8I8Transitive(int? value,long fallback)=>new int?(value.GetValueOrDefault(unchecked((int)Relay<long>(fallback,unchecked(fallback+1),unchecked(fallback+2),unchecked(fallback+3),unchecked(fallback+4),unchecked(fallback+5),unchecked(fallback+6),unchecked(fallback+7)))));
        public static long UseI8Transitive(int input,long delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructZeroCopy8I8Transitive(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
    }
    public static long ReadStructZero8I4OwnerI4(int input,int delta)=>StructZero8Owner<int>.UseI4(input,delta);
    public static long ReadStructZero8I4OwnerI4Transitive(int input,int delta)=>StructZero8Owner<int>.UseI4Transitive(input,delta);
    public static long ReadStructZero8I4OwnerI8(int input,int delta)=>StructZero8Owner<long>.UseI4(input,delta);
    public static long ReadStructZero8I4OwnerI8Transitive(int input,int delta)=>StructZero8Owner<long>.UseI4Transitive(input,delta);
    public static long ReadStructZero8I8OwnerI4(int input,long delta)=>StructZero8Owner<int>.UseI8(input,delta);
    public static long ReadStructZero8I8OwnerI4Transitive(int input,long delta)=>StructZero8Owner<int>.UseI8Transitive(input,delta);
    public static long ReadStructZero8I8OwnerI8(int input,long delta)=>StructZero8Owner<long>.UseI8(input,delta);
    public static long ReadStructZero8I8OwnerI8Transitive(int input,long delta)=>StructZero8Owner<long>.UseI8Transitive(input,delta);
    public static long ScalarStructZero8I4(int input,int delta)=>((long)input<<32)^delta^(input<0?0:unchecked(input+(int)delta));
    public static long ScalarStructZero8I8(int input,long delta)=>((long)input<<32)^delta^(input<0?0:unchecked(input+(int)delta));
    private static class StructArity3Owner<O> where O:struct {
        private static U Adjust<U>(U p0,U p1,U p2) where U:struct=>p2;
        private static U Relay<U>(U p0,U p1,U p2) where U:struct=>Adjust<U>(p0,p1,p2);
        private static int? StructArityCopy3I4(int? value,int fallback)=>new int?(value.GetValueOrDefault(Adjust<int>(fallback,unchecked(fallback+1),unchecked(fallback+2))));
        public static long UseI4(int input,int delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructArityCopy3I4(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
        private static int? StructArityCopy3I4Transitive(int? value,int fallback)=>new int?(value.GetValueOrDefault(Relay<int>(fallback,unchecked(fallback+1),unchecked(fallback+2))));
        public static long UseI4Transitive(int input,int delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructArityCopy3I4Transitive(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
        private static int? StructArityCopy3I8(int? value,long fallback)=>new int?(value.GetValueOrDefault(unchecked((int)Adjust<long>(fallback,unchecked(fallback+1),unchecked(fallback+2)))));
        public static long UseI8(int input,long delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructArityCopy3I8(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
        private static int? StructArityCopy3I8Transitive(int? value,long fallback)=>new int?(value.GetValueOrDefault(unchecked((int)Relay<long>(fallback,unchecked(fallback+1),unchecked(fallback+2)))));
        public static long UseI8Transitive(int input,long delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructArityCopy3I8Transitive(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
    }
    public static long ReadStructArity3I4OwnerI4(int input,int delta)=>StructArity3Owner<int>.UseI4(input,delta);
    public static long ReadStructArity3I4OwnerI4Transitive(int input,int delta)=>StructArity3Owner<int>.UseI4Transitive(input,delta);
    public static long ReadStructArity3I4OwnerI8(int input,int delta)=>StructArity3Owner<long>.UseI4(input,delta);
    public static long ReadStructArity3I4OwnerI8Transitive(int input,int delta)=>StructArity3Owner<long>.UseI4Transitive(input,delta);
    public static long ReadStructArity3I8OwnerI4(int input,long delta)=>StructArity3Owner<int>.UseI8(input,delta);
    public static long ReadStructArity3I8OwnerI4Transitive(int input,long delta)=>StructArity3Owner<int>.UseI8Transitive(input,delta);
    public static long ReadStructArity3I8OwnerI8(int input,long delta)=>StructArity3Owner<long>.UseI8(input,delta);
    public static long ReadStructArity3I8OwnerI8Transitive(int input,long delta)=>StructArity3Owner<long>.UseI8Transitive(input,delta);
    public static long ScalarStructArity3I4(int input,int delta)=>((long)input<<32)^delta^(input<0?unchecked((int)delta+2):unchecked(input+(int)delta));
    public static long ScalarStructArity3I8(int input,long delta)=>((long)input<<32)^delta^(input<0?unchecked((int)delta+2):unchecked(input+(int)delta));
    private static class StructArity4Owner<O> where O:struct {
        private static U Adjust<U>(U p0,U p1,U p2,U p3) where U:struct=>p3;
        private static U Relay<U>(U p0,U p1,U p2,U p3) where U:struct=>Adjust<U>(p0,p1,p2,p3);
        private static int? StructArityCopy4I4(int? value,int fallback)=>new int?(value.GetValueOrDefault(Adjust<int>(fallback,unchecked(fallback+1),unchecked(fallback+2),unchecked(fallback+3))));
        public static long UseI4(int input,int delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructArityCopy4I4(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
        private static int? StructArityCopy4I4Transitive(int? value,int fallback)=>new int?(value.GetValueOrDefault(Relay<int>(fallback,unchecked(fallback+1),unchecked(fallback+2),unchecked(fallback+3))));
        public static long UseI4Transitive(int input,int delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructArityCopy4I4Transitive(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
        private static int? StructArityCopy4I8(int? value,long fallback)=>new int?(value.GetValueOrDefault(unchecked((int)Adjust<long>(fallback,unchecked(fallback+1),unchecked(fallback+2),unchecked(fallback+3)))));
        public static long UseI8(int input,long delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructArityCopy4I8(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
        private static int? StructArityCopy4I8Transitive(int? value,long fallback)=>new int?(value.GetValueOrDefault(unchecked((int)Relay<long>(fallback,unchecked(fallback+1),unchecked(fallback+2),unchecked(fallback+3)))));
        public static long UseI8Transitive(int input,long delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructArityCopy4I8Transitive(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
    }
    public static long ReadStructArity4I4OwnerI4(int input,int delta)=>StructArity4Owner<int>.UseI4(input,delta);
    public static long ReadStructArity4I4OwnerI4Transitive(int input,int delta)=>StructArity4Owner<int>.UseI4Transitive(input,delta);
    public static long ReadStructArity4I4OwnerI8(int input,int delta)=>StructArity4Owner<long>.UseI4(input,delta);
    public static long ReadStructArity4I4OwnerI8Transitive(int input,int delta)=>StructArity4Owner<long>.UseI4Transitive(input,delta);
    public static long ReadStructArity4I8OwnerI4(int input,long delta)=>StructArity4Owner<int>.UseI8(input,delta);
    public static long ReadStructArity4I8OwnerI4Transitive(int input,long delta)=>StructArity4Owner<int>.UseI8Transitive(input,delta);
    public static long ReadStructArity4I8OwnerI8(int input,long delta)=>StructArity4Owner<long>.UseI8(input,delta);
    public static long ReadStructArity4I8OwnerI8Transitive(int input,long delta)=>StructArity4Owner<long>.UseI8Transitive(input,delta);
    public static long ScalarStructArity4I4(int input,int delta)=>((long)input<<32)^delta^(input<0?unchecked((int)delta+3):unchecked(input+(int)delta));
    public static long ScalarStructArity4I8(int input,long delta)=>((long)input<<32)^delta^(input<0?unchecked((int)delta+3):unchecked(input+(int)delta));
    private static class StructArity5Owner<O> where O:struct {
        private static U Adjust<U>(U p0,U p1,U p2,U p3,U p4) where U:struct=>p4;
        private static U Relay<U>(U p0,U p1,U p2,U p3,U p4) where U:struct=>Adjust<U>(p0,p1,p2,p3,p4);
        private static int? StructArityCopy5I4(int? value,int fallback)=>new int?(value.GetValueOrDefault(Adjust<int>(fallback,unchecked(fallback+1),unchecked(fallback+2),unchecked(fallback+3),unchecked(fallback+4))));
        public static long UseI4(int input,int delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructArityCopy5I4(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
        private static int? StructArityCopy5I4Transitive(int? value,int fallback)=>new int?(value.GetValueOrDefault(Relay<int>(fallback,unchecked(fallback+1),unchecked(fallback+2),unchecked(fallback+3),unchecked(fallback+4))));
        public static long UseI4Transitive(int input,int delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructArityCopy5I4Transitive(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
        private static int? StructArityCopy5I8(int? value,long fallback)=>new int?(value.GetValueOrDefault(unchecked((int)Adjust<long>(fallback,unchecked(fallback+1),unchecked(fallback+2),unchecked(fallback+3),unchecked(fallback+4)))));
        public static long UseI8(int input,long delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructArityCopy5I8(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
        private static int? StructArityCopy5I8Transitive(int? value,long fallback)=>new int?(value.GetValueOrDefault(unchecked((int)Relay<long>(fallback,unchecked(fallback+1),unchecked(fallback+2),unchecked(fallback+3),unchecked(fallback+4)))));
        public static long UseI8Transitive(int input,long delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructArityCopy5I8Transitive(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
    }
    public static long ReadStructArity5I4OwnerI4(int input,int delta)=>StructArity5Owner<int>.UseI4(input,delta);
    public static long ReadStructArity5I4OwnerI4Transitive(int input,int delta)=>StructArity5Owner<int>.UseI4Transitive(input,delta);
    public static long ReadStructArity5I4OwnerI8(int input,int delta)=>StructArity5Owner<long>.UseI4(input,delta);
    public static long ReadStructArity5I4OwnerI8Transitive(int input,int delta)=>StructArity5Owner<long>.UseI4Transitive(input,delta);
    public static long ReadStructArity5I8OwnerI4(int input,long delta)=>StructArity5Owner<int>.UseI8(input,delta);
    public static long ReadStructArity5I8OwnerI4Transitive(int input,long delta)=>StructArity5Owner<int>.UseI8Transitive(input,delta);
    public static long ReadStructArity5I8OwnerI8(int input,long delta)=>StructArity5Owner<long>.UseI8(input,delta);
    public static long ReadStructArity5I8OwnerI8Transitive(int input,long delta)=>StructArity5Owner<long>.UseI8Transitive(input,delta);
    public static long ScalarStructArity5I4(int input,int delta)=>((long)input<<32)^delta^(input<0?unchecked((int)delta+4):unchecked(input+(int)delta));
    public static long ScalarStructArity5I8(int input,long delta)=>((long)input<<32)^delta^(input<0?unchecked((int)delta+4):unchecked(input+(int)delta));
    private static class StructArity6Owner<O> where O:struct {
        private static U Adjust<U>(U p0,U p1,U p2,U p3,U p4,U p5) where U:struct=>p5;
        private static U Relay<U>(U p0,U p1,U p2,U p3,U p4,U p5) where U:struct=>Adjust<U>(p0,p1,p2,p3,p4,p5);
        private static int? StructArityCopy6I4(int? value,int fallback)=>new int?(value.GetValueOrDefault(Adjust<int>(fallback,unchecked(fallback+1),unchecked(fallback+2),unchecked(fallback+3),unchecked(fallback+4),unchecked(fallback+5))));
        public static long UseI4(int input,int delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructArityCopy6I4(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
        private static int? StructArityCopy6I4Transitive(int? value,int fallback)=>new int?(value.GetValueOrDefault(Relay<int>(fallback,unchecked(fallback+1),unchecked(fallback+2),unchecked(fallback+3),unchecked(fallback+4),unchecked(fallback+5))));
        public static long UseI4Transitive(int input,int delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructArityCopy6I4Transitive(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
        private static int? StructArityCopy6I8(int? value,long fallback)=>new int?(value.GetValueOrDefault(unchecked((int)Adjust<long>(fallback,unchecked(fallback+1),unchecked(fallback+2),unchecked(fallback+3),unchecked(fallback+4),unchecked(fallback+5)))));
        public static long UseI8(int input,long delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructArityCopy6I8(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
        private static int? StructArityCopy6I8Transitive(int? value,long fallback)=>new int?(value.GetValueOrDefault(unchecked((int)Relay<long>(fallback,unchecked(fallback+1),unchecked(fallback+2),unchecked(fallback+3),unchecked(fallback+4),unchecked(fallback+5)))));
        public static long UseI8Transitive(int input,long delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructArityCopy6I8Transitive(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
    }
    public static long ReadStructArity6I4OwnerI4(int input,int delta)=>StructArity6Owner<int>.UseI4(input,delta);
    public static long ReadStructArity6I4OwnerI4Transitive(int input,int delta)=>StructArity6Owner<int>.UseI4Transitive(input,delta);
    public static long ReadStructArity6I4OwnerI8(int input,int delta)=>StructArity6Owner<long>.UseI4(input,delta);
    public static long ReadStructArity6I4OwnerI8Transitive(int input,int delta)=>StructArity6Owner<long>.UseI4Transitive(input,delta);
    public static long ReadStructArity6I8OwnerI4(int input,long delta)=>StructArity6Owner<int>.UseI8(input,delta);
    public static long ReadStructArity6I8OwnerI4Transitive(int input,long delta)=>StructArity6Owner<int>.UseI8Transitive(input,delta);
    public static long ReadStructArity6I8OwnerI8(int input,long delta)=>StructArity6Owner<long>.UseI8(input,delta);
    public static long ReadStructArity6I8OwnerI8Transitive(int input,long delta)=>StructArity6Owner<long>.UseI8Transitive(input,delta);
    public static long ScalarStructArity6I4(int input,int delta)=>((long)input<<32)^delta^(input<0?unchecked((int)delta+5):unchecked(input+(int)delta));
    public static long ScalarStructArity6I8(int input,long delta)=>((long)input<<32)^delta^(input<0?unchecked((int)delta+5):unchecked(input+(int)delta));
    private static class StructArity7Owner<O> where O:struct {
        private static U Adjust<U>(U p0,U p1,U p2,U p3,U p4,U p5,U p6) where U:struct=>p6;
        private static U Relay<U>(U p0,U p1,U p2,U p3,U p4,U p5,U p6) where U:struct=>Adjust<U>(p0,p1,p2,p3,p4,p5,p6);
        private static int? StructArityCopy7I4(int? value,int fallback)=>new int?(value.GetValueOrDefault(Adjust<int>(fallback,unchecked(fallback+1),unchecked(fallback+2),unchecked(fallback+3),unchecked(fallback+4),unchecked(fallback+5),unchecked(fallback+6))));
        public static long UseI4(int input,int delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructArityCopy7I4(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
        private static int? StructArityCopy7I4Transitive(int? value,int fallback)=>new int?(value.GetValueOrDefault(Relay<int>(fallback,unchecked(fallback+1),unchecked(fallback+2),unchecked(fallback+3),unchecked(fallback+4),unchecked(fallback+5),unchecked(fallback+6))));
        public static long UseI4Transitive(int input,int delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructArityCopy7I4Transitive(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
        private static int? StructArityCopy7I8(int? value,long fallback)=>new int?(value.GetValueOrDefault(unchecked((int)Adjust<long>(fallback,unchecked(fallback+1),unchecked(fallback+2),unchecked(fallback+3),unchecked(fallback+4),unchecked(fallback+5),unchecked(fallback+6)))));
        public static long UseI8(int input,long delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructArityCopy7I8(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
        private static int? StructArityCopy7I8Transitive(int? value,long fallback)=>new int?(value.GetValueOrDefault(unchecked((int)Relay<long>(fallback,unchecked(fallback+1),unchecked(fallback+2),unchecked(fallback+3),unchecked(fallback+4),unchecked(fallback+5),unchecked(fallback+6)))));
        public static long UseI8Transitive(int input,long delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructArityCopy7I8Transitive(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
    }
    public static long ReadStructArity7I4OwnerI4(int input,int delta)=>StructArity7Owner<int>.UseI4(input,delta);
    public static long ReadStructArity7I4OwnerI4Transitive(int input,int delta)=>StructArity7Owner<int>.UseI4Transitive(input,delta);
    public static long ReadStructArity7I4OwnerI8(int input,int delta)=>StructArity7Owner<long>.UseI4(input,delta);
    public static long ReadStructArity7I4OwnerI8Transitive(int input,int delta)=>StructArity7Owner<long>.UseI4Transitive(input,delta);
    public static long ReadStructArity7I8OwnerI4(int input,long delta)=>StructArity7Owner<int>.UseI8(input,delta);
    public static long ReadStructArity7I8OwnerI4Transitive(int input,long delta)=>StructArity7Owner<int>.UseI8Transitive(input,delta);
    public static long ReadStructArity7I8OwnerI8(int input,long delta)=>StructArity7Owner<long>.UseI8(input,delta);
    public static long ReadStructArity7I8OwnerI8Transitive(int input,long delta)=>StructArity7Owner<long>.UseI8Transitive(input,delta);
    public static long ScalarStructArity7I4(int input,int delta)=>((long)input<<32)^delta^(input<0?unchecked((int)delta+6):unchecked(input+(int)delta));
    public static long ScalarStructArity7I8(int input,long delta)=>((long)input<<32)^delta^(input<0?unchecked((int)delta+6):unchecked(input+(int)delta));
    private static class StructArity8Owner<O> where O:struct {
        private static U Adjust<U>(U p0,U p1,U p2,U p3,U p4,U p5,U p6,U p7) where U:struct=>p7;
        private static U Relay<U>(U p0,U p1,U p2,U p3,U p4,U p5,U p6,U p7) where U:struct=>Adjust<U>(p0,p1,p2,p3,p4,p5,p6,p7);
        private static int? StructArityCopy8I4(int? value,int fallback)=>new int?(value.GetValueOrDefault(Adjust<int>(fallback,unchecked(fallback+1),unchecked(fallback+2),unchecked(fallback+3),unchecked(fallback+4),unchecked(fallback+5),unchecked(fallback+6),unchecked(fallback+7))));
        public static long UseI4(int input,int delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructArityCopy8I4(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
        private static int? StructArityCopy8I4Transitive(int? value,int fallback)=>new int?(value.GetValueOrDefault(Relay<int>(fallback,unchecked(fallback+1),unchecked(fallback+2),unchecked(fallback+3),unchecked(fallback+4),unchecked(fallback+5),unchecked(fallback+6),unchecked(fallback+7))));
        public static long UseI4Transitive(int input,int delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructArityCopy8I4Transitive(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
        private static int? StructArityCopy8I8(int? value,long fallback)=>new int?(value.GetValueOrDefault(unchecked((int)Adjust<long>(fallback,unchecked(fallback+1),unchecked(fallback+2),unchecked(fallback+3),unchecked(fallback+4),unchecked(fallback+5),unchecked(fallback+6),unchecked(fallback+7)))));
        public static long UseI8(int input,long delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructArityCopy8I8(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
        private static int? StructArityCopy8I8Transitive(int? value,long fallback)=>new int?(value.GetValueOrDefault(unchecked((int)Relay<long>(fallback,unchecked(fallback+1),unchecked(fallback+2),unchecked(fallback+3),unchecked(fallback+4),unchecked(fallback+5),unchecked(fallback+6),unchecked(fallback+7)))));
        public static long UseI8Transitive(int input,long delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructArityCopy8I8Transitive(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
    }
    public static long ReadStructArity8I4OwnerI4(int input,int delta)=>StructArity8Owner<int>.UseI4(input,delta);
    public static long ReadStructArity8I4OwnerI4Transitive(int input,int delta)=>StructArity8Owner<int>.UseI4Transitive(input,delta);
    public static long ReadStructArity8I4OwnerI8(int input,int delta)=>StructArity8Owner<long>.UseI4(input,delta);
    public static long ReadStructArity8I4OwnerI8Transitive(int input,int delta)=>StructArity8Owner<long>.UseI4Transitive(input,delta);
    public static long ReadStructArity8I8OwnerI4(int input,long delta)=>StructArity8Owner<int>.UseI8(input,delta);
    public static long ReadStructArity8I8OwnerI4Transitive(int input,long delta)=>StructArity8Owner<int>.UseI8Transitive(input,delta);
    public static long ReadStructArity8I8OwnerI8(int input,long delta)=>StructArity8Owner<long>.UseI8(input,delta);
    public static long ReadStructArity8I8OwnerI8Transitive(int input,long delta)=>StructArity8Owner<long>.UseI8Transitive(input,delta);
    public static long ScalarStructArity8I4(int input,int delta)=>((long)input<<32)^delta^(input<0?unchecked((int)delta+7):unchecked(input+(int)delta));
    public static long ScalarStructArity8I8(int input,long delta)=>((long)input<<32)^delta^(input<0?unchecked((int)delta+7):unchecked(input+(int)delta));
    private static class StructBinaryOwner<O> where O:struct {
        private static U Adjust<U>(U value,U other) where U:struct=>other;
        private static U Relay<U>(U value,U other) where U:struct=>Adjust<U>(value,other);
        private static int? StructBinaryCopyI4(int? value,int fallback)=>new int?(value.GetValueOrDefault(Adjust<int>(fallback,unchecked(fallback+1))));
        private static int? StructBinaryCopyI8(int? value,long fallback)=>new int?(value.GetValueOrDefault(unchecked((int)Adjust<long>(fallback,unchecked(fallback+1)))));
        private static int? StructBinaryCopyI4Transitive(int? value,int fallback)=>new int?(value.GetValueOrDefault(Relay<int>(fallback,unchecked(fallback+1))));
        private static int? StructBinaryCopyI8Transitive(int? value,long fallback)=>new int?(value.GetValueOrDefault(unchecked((int)Relay<long>(fallback,unchecked(fallback+1)))));
        public static long UseI4(int input,int delta){int? value=input<0?null:new int?(unchecked(input+delta));var shifted=StructBinaryCopyI4(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
        public static long UseI8(int input,long delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructBinaryCopyI8(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
        public static long UseI4Transitive(int input,int delta){int? value=input<0?null:new int?(unchecked(input+delta));var shifted=StructBinaryCopyI4Transitive(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
        public static long UseI8Transitive(int input,long delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));var shifted=StructBinaryCopyI8Transitive(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
    }
    public static long ReadStructBinaryI4OwnerI4(int input,int delta)=>StructBinaryOwner<int>.UseI4(input,delta);
    public static long ReadStructBinaryI4OwnerI8(int input,int delta)=>StructBinaryOwner<long>.UseI4(input,delta);
    public static long ReadStructBinaryI8OwnerI4(int input,long delta)=>StructBinaryOwner<int>.UseI8(input,delta);
    public static long ReadStructBinaryI8OwnerI8(int input,long delta)=>StructBinaryOwner<long>.UseI8(input,delta);
    public static long ReadStructBinaryI4OwnerI4Transitive(int input,int delta)=>StructBinaryOwner<int>.UseI4Transitive(input,delta);
    public static long ReadStructBinaryI4OwnerI8Transitive(int input,int delta)=>StructBinaryOwner<long>.UseI4Transitive(input,delta);
    public static long ReadStructBinaryI8OwnerI4Transitive(int input,long delta)=>StructBinaryOwner<int>.UseI8Transitive(input,delta);
    public static long ReadStructBinaryI8OwnerI8Transitive(int input,long delta)=>StructBinaryOwner<long>.UseI8Transitive(input,delta);
    public static long ScalarStructBinaryI4(int input,int delta)=>((long)input<<32)^delta^(input<0?unchecked(delta+1):unchecked(input+delta));
    public static long ScalarStructBinaryI8(int input,long delta)=>((long)input<<32)^delta^(input<0?unchecked((int)delta+1):unchecked(input+(int)delta));
    private static class StructWideOwner<O> where O:struct {
        private static U Adjust<U>(U value) where U:struct=>value;
        private static int? StructWideCopy(int? value,long fallback)=>new int?(value.GetValueOrDefault(unchecked((int)Adjust<long>(fallback))));
        public static long Use(int input,long delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));int? shifted=StructWideCopy(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
    }
    private static class StructWideTransitiveOwner<O> where O:struct {
        private static U Step<U>(U value) where U:struct=>value;
        private static U Adjust<U>(U value) where U:struct=>Step<U>(value);
        private static int? StructWideTransitiveCopy(int? value,long fallback)=>new int?(value.GetValueOrDefault(unchecked((int)Adjust<long>(fallback))));
        public static long Use(int input,long delta){int? value=input<0?null:new int?(unchecked(input+(int)delta));int? shifted=StructWideTransitiveCopy(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
    }
    public static long ReadStructWideI4(int input,long delta)=>StructWideOwner<int>.Use(input,delta);
    public static long ReadStructWideI8(int input,long delta)=>StructWideOwner<long>.Use(input,delta);
    public static long ReadStructWideI4Transitive(int input,long delta)=>StructWideTransitiveOwner<int>.Use(input,delta);
    public static long ReadStructWideI8Transitive(int input,long delta)=>StructWideTransitiveOwner<long>.Use(input,delta);
    public static long ScalarStructWide(int input,long delta)=>((long)input<<32)^delta^(input<0?unchecked((int)delta):unchecked(input+(int)delta));
    private static class StructScalarMethodOwner<O> where O:struct {
        private static U Adjust<U>(U value) where U:struct=>value;
        private static int? StructScalarMethodCopy(int? value,int fallback)=>new int?(value.GetValueOrDefault(Adjust<int>(fallback)));
        public static long Use(int input,int delta){int? value=input<0?null:new int?(unchecked(input+delta));int? shifted=StructScalarMethodCopy(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
    }
    private static class StructScalarMethodTransitiveOwner<O> where O:struct {
        private static U Step<U>(U value) where U:struct=>value;
        private static U Adjust<U>(U value) where U:struct=>Step<U>(value);
        private static int? StructScalarMethodTransitiveCopy(int? value,int fallback)=>new int?(value.GetValueOrDefault(Adjust<int>(fallback)));
        public static long Use(int input,int delta){int? value=input<0?null:new int?(unchecked(input+delta));int? shifted=StructScalarMethodTransitiveCopy(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
    }
    public static long ReadStructScalarMethodI4(int input,int delta)=>StructScalarMethodOwner<int>.Use(input,delta);
    public static long ReadStructScalarMethodI8(int input,int delta)=>StructScalarMethodOwner<long>.Use(input,delta);
    public static long ReadStructScalarMethodI4Transitive(int input,int delta)=>StructScalarMethodTransitiveOwner<int>.Use(input,delta);
    public static long ReadStructScalarMethodI8Transitive(int input,int delta)=>StructScalarMethodTransitiveOwner<long>.Use(input,delta);
    public static long ScalarStructScalarMethod(int input,int delta)=>((long)input<<32)^delta^(input<0?delta:unchecked(input+delta));
    private static class StructScalarOwner<O> where O:struct {
        private static int Adjust(int value)=>unchecked(value+1);
        private static int? StructScalarCopy(int? value,int fallback)=>new int?(value.GetValueOrDefault(Adjust(fallback)));
        public static long Use(int input,int delta){int? value=input<0?null:new int?(unchecked(input+delta));int? shifted=StructScalarCopy(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
    }
    private static class StructScalarTransitiveOwner<O> where O:struct {
        private static int Step(int value)=>unchecked(value+1);
        private static int Adjust(int value)=>Step(value);
        private static int? StructScalarTransitiveCopy(int? value,int fallback)=>new int?(value.GetValueOrDefault(Adjust(fallback)));
        public static long Use(int input,int delta){int? value=input<0?null:new int?(unchecked(input+delta));int? shifted=StructScalarTransitiveCopy(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
    }
    public static long ReadStructScalarI4(int input,int delta)=>StructScalarOwner<int>.Use(input,delta);
    public static long ReadStructScalarI8(int input,int delta)=>StructScalarOwner<long>.Use(input,delta);
    public static long ReadStructScalarI4Transitive(int input,int delta)=>StructScalarTransitiveOwner<int>.Use(input,delta);
    public static long ReadStructScalarI8Transitive(int input,int delta)=>StructScalarTransitiveOwner<long>.Use(input,delta);
    public static long ScalarStructScalar(int input,int delta)=>((long)input<<32)^delta^(input<0?unchecked(delta+1):unchecked(input+delta));
    private static T? GenericPayloadCopy<T>(T? value) where T:struct=>value;
    private static T? GenericFactoryCopy<T>(T? value) where T:struct=>new T?(value.GetValueOrDefault());
    private static T? GenericDefaultCopy<T>(T? value) where T:struct=>new T?(value.GetValueOrDefault(default(T)));
    private static T? GenericFallbackCopy<T>(T? value,T fallback) where T:struct=>new T?(value.GetValueOrDefault(fallback));
    private static class FallbackOwner<O> {
        private static T? OwnerFallbackCopy<T>(T? value,T fallback) where T:struct=>new T?(value.GetValueOrDefault(fallback));
        public static long Use(int input,int delta){int? value=input<0?null:new int?(unchecked(input+delta));int? shifted=OwnerFallbackCopy<int>(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
    }
    public static long ReadOwnerFallbackI4(int input,int delta)=>FallbackOwner<int>.Use(input,delta);
    public static long ReadOwnerFallbackI8(int input,int delta)=>FallbackOwner<long>.Use(input,delta);
    private static class StructFallbackOwner<O> where O:struct {
        private static T? StructOwnerFallbackCopy<T>(T? value,T fallback) where T:struct=>new T?(value.GetValueOrDefault(fallback));
        public static long Use(int input,int delta){int? value=input<0?null:new int?(unchecked(input+delta));int? shifted=StructOwnerFallbackCopy<int>(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
    }
    public static long ReadStructOwnerFallbackI4(int input,int delta)=>StructFallbackOwner<int>.Use(input,delta);
    public static long ReadStructOwnerFallbackI8(int input,int delta)=>StructFallbackOwner<long>.Use(input,delta);
    public static long ReadGenericFallback(int input,int delta){int? value=input<0?null:new int?(unchecked(input+delta));int? shifted=GenericFallbackCopy<int>(value,delta);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
    public static long ScalarGenericFallback(int input,int delta)=>((long)input<<32)^delta^(input<0?delta:unchecked(input+delta));
    public static long ReadGenericDefault(int input,int delta){int? value=input<0?null:new int?(unchecked(input+delta));int? shifted=GenericDefaultCopy<int>(value);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
    public static long ReadGenericFactory(int input,int delta){int? value=input<0?null:new int?(unchecked(input+delta));int? shifted=GenericFactoryCopy<int>(value);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
    public static long ScalarGenericFactory(int input,int delta)=>((long)input<<32)^delta^(input<0?0:unchecked(input+delta));
    private static T? GenericPresenceCopy<T>(T? value) where T:struct=>value.HasValue?value:null;
    public static long ReadGenericPresence(int input,int delta){int? value=input<0?null:unchecked(input+delta);int? shifted=GenericPresenceCopy<int>(value);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
    public static long ReadGenericPayload(int input,int delta){int? value=input<0?null:unchecked(input+delta);int? shifted=GenericPayloadCopy<int>(value);return ((long)input<<32)^delta^shifted.GetValueOrDefault(-7);}
    public static long ScalarGenericPayload(int input,int delta)=>((long)input<<32)^delta^(input<0?-7:unchecked(input+delta));
    private static class GenericMapOwner<T> {
        private static int Adjust<V>(int value)=>unchecked(value+1);
        private static int? Map<U>(int? value,int delta)=>value.HasValue?unchecked(value.GetValueOrDefault()+Adjust<U>(delta)):null;
        private static int? TypeMap(int? value,int delta)=>value.HasValue?unchecked(value.GetValueOrDefault()+Adjust<T>(delta)):null;
        public static long Method<U>(int input,int delta){int? value=input<0?null:input;int? result=Map<U>(value,delta);return ((long)input<<32)^delta^result.GetValueOrDefault(-7);}
        public static long Type(int input,int delta){int? value=input<0?null:input;int? result=TypeMap(value,delta);return ((long)input<<32)^delta^result.GetValueOrDefault(-7);}
    }
    private static int GenericAdjust<V>(int value)=>unchecked(value+1);
    private static int? GenericMap<U>(int? value,int delta)=>value.HasValue?unchecked(value.GetValueOrDefault()+GenericAdjust<U>(delta)):null;
    private static long GenericUse<U>(int input,int delta){int? value=input<0?null:input;int? result=GenericMap<U>(value,delta);return ((long)input<<32)^delta^result.GetValueOrDefault(-7);}
    public static long ReadGenericMethodI4(int input,int delta)=>GenericUse<int>(input,delta);
    public static long ReadGenericBoth(int input,int delta){int? value=input<0?null:input;int? first=GenericMap<int>(value,delta);int? result=GenericMap<long>(first,delta);return ((long)input<<32)^delta^result.GetValueOrDefault(-7);}
    public static long ReadGenericMethodI8(int input,int delta)=>GenericUse<long>(input,delta);
    public static long ReadGenericTypeI4(int input,int delta)=>GenericMapOwner<int>.Type(input,delta);
    public static long ReadGenericTypeI8(int input,int delta)=>GenericMapOwner<long>.Type(input,delta);
    public static long ReadGenericCombinedI4I8(int input,int delta)=>GenericMapOwner<int>.Method<long>(input,delta);
    public static long ReadGenericCombinedI8I4(int input,int delta)=>GenericMapOwner<long>.Method<int>(input,delta);
    public static long ScalarGenericMap(int input,int delta)=>((long)input<<32)^delta^(input<0?-7:unchecked(input+delta+1));
    private static int? ShiftLoopSwitch(int? value,int count,int delta){if(!value.HasValue)return null;int payload=value.GetValueOrDefault();for(int i=0;i<count;i++){switch(i&3){case 0:payload=unchecked(payload+delta);break;case 1:payload=unchecked(payload-delta);break;case 2:payload^=delta;break;default:payload=~payload;break;}}return payload;}
    public static long ReadLoopSwitch(int input,int present,int count,int delta){int? value=present==0?null:input;int? result=ShiftLoopSwitch(value,count,delta);return ((long)input<<32)^count^delta^result.GetValueOrDefault(-7);}
    public static long ScalarLoopSwitch(int input,int present,int count,int delta){if(present==0)return ((long)input<<32)^count^delta^-7L;int payload=input;for(int i=0;i<count;i++){int selector=i&3;if(selector==0)payload=unchecked(payload+delta);else if(selector==1)payload=unchecked(payload-delta);else if(selector==2)payload^=delta;else payload=~payload;}return ((long)input<<32)^count^delta^payload;}
    public static long WordBranch(int input,int delta)=>unchecked(input+delta)<0?-7L:11L;
    public static long WordZero(int input,int delta)=>unchecked(input*delta)==0?3L:9L;
    public static long WordUnsigned(int input,int delta)=>unchecked((uint)(input+delta))<0x80000001U?5L:13L;
    public static long WordCeq(int input,int delta)=>(long)(unchecked(input+delta)==int.MinValue?1:0);
    public static long WordClt(int input,int delta)=>(long)(unchecked(input+delta)<0?1:0);
    public static long WordCltu(int input,int delta)=>(long)(unchecked((uint)(input+delta))<0x80000001U?1:0);
    public static long WordSwitch(int input,int delta)=>unchecked(input*delta) switch{0=>23L,1=>29L,2=>31L,3=>37L,_=>41L};
    private static int LoopAdjust(int input,int delta)=>unchecked(input+delta);
    private static int? ShiftLoopChecked(int? value,int count,int delta){if(!value.HasValue)return null;int payload=value.Value;for(int i=0;i<count;i++)payload=unchecked(payload+delta);return payload;}
    private static int? ProduceLoopI4(int input,int present,int count,int delta){if(present==0)return null;int payload=input;for(int i=0;i<count;i++)payload=unchecked(payload+delta);return payload;}
    public static long ReadLoopChecked(int input,int present,int count,int delta){int? value=present==0?null:input;int? result=ShiftLoopChecked(value,count,delta);return ((long)input<<32)^count^delta^result.GetValueOrDefault(-7);}
    public static long ReadLoopProducer(int input,int present,int count,int delta){int? result=ProduceLoopI4(input,present,count,delta);return ((long)input<<32)^count^delta^result.GetValueOrDefault(-7);}
    private static int? ShiftLoopI4(int? value,int count,int delta){if(!value.HasValue)return null;int payload=value.GetValueOrDefault();for(int i=0;i<count;i++)payload=unchecked(payload+delta);return payload;}
    private static int? ShiftLoopI8(int? value,long count,int delta){if(!value.HasValue)return null;int payload=value.GetValueOrDefault();for(long i=0;i<count;i++)payload=unchecked(payload+delta);return payload;}
    private static int? ShiftLoopU4(int? value,int count,int delta){if(!value.HasValue)return null;int payload=value.GetValueOrDefault();for(uint i=0x7ffffffeU;i<0x80000001U;i++)payload=unchecked(payload+delta);return payload;}
    private static int? ShiftLoopU8(int? value,int count,int delta){if(!value.HasValue)return null;int payload=value.GetValueOrDefault();for(ulong i=0x7ffffffffffffffeUL;i<0x8000000000000001UL;i++)payload=unchecked(payload+delta);return payload;}
    private static int? ShiftLoopHelper(int? value,int count,int delta){if(!value.HasValue)return null;int payload=value.GetValueOrDefault();for(int i=0;i<count;i++)payload=LoopAdjust(payload,delta);return payload;}
    public static long ReadLoopI4(int input,int present,int count,int delta){int? value=present==0?null:input;int? result=ShiftLoopI4(value,count,delta);return ((long)input<<32)^count^delta^result.GetValueOrDefault(-7);}
    public static long ReadLoopI8(int input,int present,long count,int delta){int? value=present==0?null:input;int? result=ShiftLoopI8(value,count,delta);return ((long)input<<32)^count^delta^result.GetValueOrDefault(-7);}
    public static long ReadLoopU4(int input,int present,int count,int delta){int? value=present==0?null:input;int? result=ShiftLoopU4(value,count,delta);return ((long)input<<32)^count^delta^result.GetValueOrDefault(-7);}
    public static long ReadLoopU8(int input,int present,int count,int delta){int? value=present==0?null:input;int? result=ShiftLoopU8(value,count,delta);return ((long)input<<32)^count^delta^result.GetValueOrDefault(-7);}
    public static long ReadLoopHelper(int input,int present,int count,int delta){int? value=present==0?null:input;int? result=ShiftLoopHelper(value,count,delta);return ((long)input<<32)^count^delta^result.GetValueOrDefault(-7);}
    public static long ScalarLoopI4(int input,int present,int count,int delta)=>((long)input<<32)^count^delta^(present==0?-7:unchecked(input+delta*(count>0?count:0)));
    public static long ScalarLoopI8(int input,int present,long count,int delta)=>((long)input<<32)^count^delta^(present==0?-7:unchecked(input+delta*(count>0?(int)count:0)));
    public static long ScalarLoopU4(int input,int present,int count,int delta)=>((long)input<<32)^count^delta^(present==0?-7:unchecked(input+delta*3));
    public static long ScalarLoopU8(int input,int present,int count,int delta)=>((long)input<<32)^count^delta^(present==0?-7:unchecked(input+delta*3));
    public static long ScalarLoopHelper(int input,int present,int count,int delta)=>((long)input<<32)^count^delta^(present==0?-7:unchecked(input+delta*(count>0?count:0)));
    private static int? ShiftSwitchExpression(int? value,int scale,int selector)=>unchecked(value.GetValueOrDefault(selector switch{0=>scale,1=>scale+1,2=>scale-1,3=>0,_=>selector})*scale);
    private static int? ShiftSwitchStatement(int? value,int scale,int selector){switch(selector){case 0:return null;case 1:return value;case 2:return unchecked(value.GetValueOrDefault()+scale);case 3:return unchecked(value.GetValueOrDefault()-scale);default:return unchecked(value.GetValueOrDefault()*scale);}}
    public static long ReadSwitchExpression(int input,int present,int scale,int selector){int? value=present==0?null:input;int? result=ShiftSwitchExpression(value,scale,selector);return ((long)input<<32)^scale^selector^result.GetValueOrDefault(-7);}
    public static long ReadSwitchStatement(int input,int present,int scale,int selector){int? value=present==0?null:input;int? result=ShiftSwitchStatement(value,scale,selector);return ((long)input<<32)^scale^selector^result.GetValueOrDefault(-7);}
    public static long ScalarSwitchExpression(int input,int present,int scale,int selector)=>((long)input<<32)^scale^selector^unchecked((present==0?(selector==0?scale:selector==1?scale+1:selector==2?scale-1:selector==3?0:selector):input)*scale);
    public static long ScalarSwitchStatement(int input,int present,int scale,int selector)=>((long)input<<32)^scale^selector^(selector==0?-7:selector==1?(present==0?-7:input):selector==2?unchecked((present==0?0:input)+scale):selector==3?unchecked((present==0?0:input)-scale):unchecked((present==0?0:input)*scale));
    private static int AdjustFallback(int input)=>unchecked(input+1);
    private static int? ShiftFallbackThreeHelper(int? value,int scale,int fallback)=>unchecked(value.GetValueOrDefault(AdjustFallback(fallback))*scale);
    public static long ReadFallbackThreeHelper(int input,int scale,int fallback){int? value=input==0?null:input;int? result=ShiftFallbackThreeHelper(value,scale,fallback);return ((long)input<<32)^fallback^result.GetValueOrDefault(-7);}
    public static long ScalarFallbackThreeHelper(int input,int scale,int fallback)=>((long)input<<32)^fallback^unchecked((input==0?fallback+1:input)*scale);
    private static int? ShiftFallbackThreeComputed(int? value,int scale,int fallback)=>unchecked(value.GetValueOrDefault(unchecked(fallback+1))*scale);
    private static int? ShiftFallbackThreeAssignedComputed(int? value,int scale,int fallback){fallback=unchecked(fallback+1);return unchecked(value.GetValueOrDefault(unchecked(fallback+1))*scale);}
    public static long ReadFallbackThreeComputed(int input,int scale,int fallback){int? value=input==0?null:input;int? result=ShiftFallbackThreeComputed(value,scale,fallback);return ((long)input<<32)^fallback^result.GetValueOrDefault(-7);}
    public static long ReadFallbackThreeAssignedComputed(int input,int scale,int fallback){int? value=input==0?null:input;int? result=ShiftFallbackThreeAssignedComputed(value,scale,fallback);return ((long)input<<32)^fallback^result.GetValueOrDefault(-7);}
    public static long ScalarFallbackThreeComputed(int input,int scale,int fallback)=>((long)input<<32)^fallback^unchecked((input==0?fallback+1:input)*scale);
    public static long ScalarFallbackThreeAssignedComputed(int input,int scale,int fallback)=>((long)input<<32)^fallback^unchecked((input==0?fallback+2:input)*scale);
    private static int? ShiftFallbackEightComputed(int? value,int scale,long delta,int a,long b,int c,long d,int fallback)=>unchecked(value.GetValueOrDefault(unchecked(fallback+1))*scale);
    private static int? ShiftFallbackEightAssignedComputed(int? value,int scale,long delta,int a,long b,int c,long d,int fallback){fallback=unchecked(fallback+1);return unchecked(value.GetValueOrDefault(unchecked(fallback+1))*scale);}
    public static long ReadFallbackEightComputed(int input,int scale,long delta,int a,long b,int c,long d,int fallback){int? value=input==0?null:input;int? result=ShiftFallbackEightComputed(value,scale,delta,a,b,c,d,fallback);return ((long)input<<32)^delta^fallback^result.GetValueOrDefault(-7);}
    public static long ReadFallbackEightAssignedComputed(int input,int scale,long delta,int a,long b,int c,long d,int fallback){int? value=input==0?null:input;int? result=ShiftFallbackEightAssignedComputed(value,scale,delta,a,b,c,d,fallback);return ((long)input<<32)^delta^fallback^result.GetValueOrDefault(-7);}
    public static long ScalarFallbackEightComputed(int input,int scale,long delta,int a,long b,int c,long d,int fallback)=>((long)input<<32)^delta^fallback^unchecked((input==0?fallback+1:input)*scale);
    public static long ScalarFallbackEightAssignedComputed(int input,int scale,long delta,int a,long b,int c,long d,int fallback)=>((long)input<<32)^delta^fallback^unchecked((input==0?fallback+2:input)*scale);
    private static int? ShiftFallbackThree(int? value,int scale,int fallback)=>unchecked(value.GetValueOrDefault(fallback)*scale);
    private static int? ShiftFallbackThreeAssigned(int? value,int scale,int fallback){fallback=unchecked(fallback+1);return unchecked(value.GetValueOrDefault(fallback)*scale);}
    public static long ReadFallbackThree(int input,int scale,int fallback){int? value=input==0?null:input;int? result=ShiftFallbackThree(value,scale,fallback);return ((long)input<<32)^fallback^result.GetValueOrDefault(-7);}
    public static long ReadFallbackThreeAssigned(int input,int scale,int fallback){int? value=input==0?null:input;int? result=ShiftFallbackThreeAssigned(value,scale,fallback);return ((long)input<<32)^fallback^result.GetValueOrDefault(-7);}
    public static long ScalarFallbackThree(int input,int scale,int fallback)=>((long)input<<32)^fallback^unchecked((input==0?fallback:input)*scale);
    public static long ScalarFallbackThreeAssigned(int input,int scale,int fallback)=>((long)input<<32)^fallback^unchecked((input==0?fallback+1:input)*scale);
    private static int? ShiftFallbackEight(int? value,int scale,long delta,int a,long b,int c,long d,int fallback)=>unchecked(value.GetValueOrDefault(fallback)*scale);
    private static int? ShiftFallbackEightAssigned(int? value,int scale,long delta,int a,long b,int c,long d,int fallback){fallback=unchecked(fallback+1);return unchecked(value.GetValueOrDefault(fallback)*scale);}
    public static long ReadFallbackEight(int input,int scale,long delta,int a,long b,int c,long d,int fallback){int? value=input==0?null:input;int? result=ShiftFallbackEight(value,scale,delta,a,b,c,d,fallback);return ((long)input<<32)^delta^fallback^result.GetValueOrDefault(-7);}
    public static long ReadFallbackEightAssigned(int input,int scale,long delta,int a,long b,int c,long d,int fallback){int? value=input==0?null:input;int? result=ShiftFallbackEightAssigned(value,scale,delta,a,b,c,d,fallback);return ((long)input<<32)^delta^fallback^result.GetValueOrDefault(-7);}
    public static long ScalarFallbackEight(int input,int scale,long delta,int a,long b,int c,long d,int fallback)=>((long)input<<32)^delta^fallback^unchecked((input==0?fallback:input)*scale);
    public static long ScalarFallbackEightAssigned(int input,int scale,long delta,int a,long b,int c,long d,int fallback)=>((long)input<<32)^delta^fallback^unchecked((input==0?fallback+1:input)*scale);
    private static int? ShiftMappedScaled(int? value,int scale,long delta)=>value.HasValue?unchecked(value.GetValueOrDefault()*scale+(int)delta):null;
    public static long ReadMappedScaled(int input,int scale,long delta){int? value=input==0?null:input;int? result=ShiftMappedScaled(value,scale,delta);return ((long)input<<32)^delta^result.GetValueOrDefault(-7);}
    public static long ScalarMappedScaled(int input,int scale,long delta)=>((long)input<<32)^delta^(input==0?-7L:unchecked(input*scale+(int)delta));
    private static int? ShiftMappedEight(int? value,int scale,long delta,int a,long b,int c,long d,int e)=>value.HasValue?unchecked(value.GetValueOrDefault()*scale+(int)delta^a^(int)b^c^(int)d^e):null;
    public static long ReadMappedEight(int input,int scale,long delta,int a,long b,int c,long d,int e){int? value=input==0?null:input;int? result=ShiftMappedEight(value,scale,delta,a,b,c,d,e);return ((long)input<<32)^delta^result.GetValueOrDefault(-7);}
    public static long ScalarMappedEight(int input,int scale,long delta,int a,long b,int c,long d,int e)=>((long)input<<32)^delta^(input==0?-7L:unchecked(input*scale+(int)delta^a^(int)b^c^(int)d^e));
    private static int? ShiftMapped(int? value)=>value.HasValue?unchecked(value.GetValueOrDefault()+1):null;
    private static int? ShiftMappedTwice(int? value)=>ShiftMapped(ShiftMapped(value));
    private static int? ShiftMappedAssign(int? value){value=value.HasValue?unchecked(value.GetValueOrDefault()+1):null;return value;}
    private static int? ShiftMappedFill(int? value){value=unchecked(value.GetValueOrDefault()+1);return value;}
    private static int InitializerAdjust(int input)=>unchecked(input+1);
    private static int? ShiftMappedInitializerHelper(int? value){value=InitializerAdjust(value.GetValueOrDefault());return value;}
    public static long ReadMappedInitializerHelper(int input,int present){int? value=present==0?null:input;int? result=ShiftMappedInitializerHelper(value);return ((long)input<<32)^result.GetValueOrDefault(-7);}
    private static int? ShiftMappedClear(int? value){value=null;return value;}
    private static int MappedPayload(int? value)=>value.GetValueOrDefault();
    private static int? ShiftMappedGetter(int? value)=>value.HasValue?unchecked(MappedPayload(value)+1):null;
    private static int? ShiftMappedI4(int? value,int delta)=>value.HasValue?unchecked(value.GetValueOrDefault()-delta):null;
    private static int? ShiftMappedI8(int? value,long delta)=>value.HasValue?unchecked(value.GetValueOrDefault()-(int)delta):null;
    public static long ReadMapped(int input,int present){int? value=present==0?null:input;int? result=ShiftMapped(value);return ((long)input<<32)^result.GetValueOrDefault(-7);}
    public static long ReadMappedTwice(int input,int present){int? value=present==0?null:input;int? result=ShiftMappedTwice(value);return ((long)input<<32)^result.GetValueOrDefault(-7);}
    public static long ReadMappedAssign(int input,int present){int? value=present==0?null:input;int? result=ShiftMappedAssign(value);return ((long)input<<32)^result.GetValueOrDefault(-7);}
    public static long ReadMappedFill(int input,int present){int? value=present==0?null:input;int? result=ShiftMappedFill(value);return ((long)input<<32)^result.GetValueOrDefault(-7);}
    public static long ReadMappedClear(int input,int present){int? value=present==0?null:input;int? result=ShiftMappedClear(value);return ((long)input<<32)^result.GetValueOrDefault(-7);}
    public static long ReadMappedGetter(int input,int present){int? value=present==0?null:input;int? result=ShiftMappedGetter(value);return ((long)input<<32)^result.GetValueOrDefault(-7);}
    public static long ScalarMapped(int input,int present)=>((long)input<<32)^(present==0?-7L:unchecked(input+1));
    public static long ScalarMappedTwice(int input,int present)=>((long)input<<32)^(present==0?-7L:unchecked(input+2));
    public static long ScalarMappedFill(int input,int present)=>((long)input<<32)^(present==0?1L:unchecked(input+1));
    public static long ScalarMappedClear(int input,int present)=>((long)input<<32)^-7L;
    public static long ReadMappedI4(int input,int delta){int? value=input==0?null:input;int? result=ShiftMappedI4(value,delta);return (long)delta^((long)input<<32)^result.GetValueOrDefault(-7);}
    public static long ReadMappedI8(int input,long delta){int? value=input==0?null:input;int? result=ShiftMappedI8(value,delta);return delta^((long)input<<32)^result.GetValueOrDefault(-7);}
    public static long ScalarMappedI4(int input,int delta)=>(long)delta^((long)input<<32)^(input==0?-7L:unchecked(input-delta));
    public static long ScalarMappedI8(int input,long delta)=>delta^((long)input<<32)^(input==0?-7L:unchecked(input-(int)delta));
    private static int ReturnHelperLeaf(int input)=>unchecked(input+1);
    private static int ReturnHelperAdjust(int input)=>ReturnHelperLeaf(input);
    private static int? OptionalReturnHelper(int input)=>input==0?null:ReturnHelperAdjust(input);
    private static int? OptionalReturnRelayLeaf(int input)=>OptionalReturnHelper(input);
    private static int? OptionalReturnRelay(int input){int? value=OptionalReturnRelayLeaf(input);int? copy=value;return copy;}
    public static long ReadReturnComposition(int input){int? value=OptionalReturnRelay(input);return ((long)input<<32)^value.GetValueOrDefault(-7);}
    public static long ReadReturnHelper(int input){int? value=OptionalReturnHelper(input);return ((long)input<<32)^value.GetValueOrDefault(-7);}
    public static long ScalarReturnHelper(int input)=>((long)input<<32)^(input==0?-7L:unchecked(input+1));
    private static int? OptionalMixed(int input,long delta)=>input<0?null:unchecked(input-(int)delta);
    private static int? OptionalMixedReversed(long delta,int input)=>input<0?null:unchecked((int)delta-input);
    private static int? RelayMixed(int input,long delta)=>OptionalMixed(input,delta);
    private static int? RelayMixedReversed(long delta,int input)=>OptionalMixedReversed(delta,input);
    public static long ReadMixedRelay(int input,long delta){int? value=RelayMixed(input,delta);return delta^((long)input<<32)^value.GetValueOrDefault(-7);}
    public static long ReadMixedRelayReversed(int input,long delta){int? value=RelayMixedReversed(delta,input);return delta^((long)input<<32)^value.GetValueOrDefault(-7);}
    public static long ReadMixedReturn(int input,long delta){int? value=OptionalMixed(input,delta);return delta^((long)input<<32)^value.GetValueOrDefault(-7);}
    public static long ReadMixedReversed(int input,long delta){int? value=OptionalMixedReversed(delta,input);return delta^((long)input<<32)^value.GetValueOrDefault(-7);}
    public static long ScalarMixedReturn(int input,long delta)=>delta^((long)input<<32)^(input<0?-7L:unchecked(input-(int)delta));
    public static long ScalarMixedReversed(int input,long delta)=>delta^((long)input<<32)^(input<0?-7L:unchecked((int)delta-input));
    private static int? OptionalBinary(int input,int delta)=>input<0?null:unchecked(input+delta);
    private static int? OptionalBinaryDifference(int input,int delta)=>input==0?null:unchecked(delta-input);
    public static long ReadBinaryOptional(int input,int delta){int? value=OptionalBinary(input,delta);return value.GetValueOrDefault(-7);}
    public static long ReadBinaryDifference(int input,int delta){int? value=OptionalBinaryDifference(input,delta);return value.GetValueOrDefault(-7);}
    public static long ReadBinaryPreserved(int input,int delta){int? value=OptionalBinary(input,delta);return ((long)input<<32)^(long)delta^value.GetValueOrDefault(-7);}
    public static long ReadBinaryTwice(int input,int delta){int? left=OptionalBinary(input,delta);int? right=OptionalBinaryDifference(delta,input);return (long)left.GetValueOrDefault(-7)+right.GetValueOrDefault(-9);}
    public static long ScalarBinaryOptional(int input,int delta)=>input<0?-7L:unchecked(input+delta);
    public static long ScalarBinaryDifference(int input,int delta)=>input==0?-7L:unchecked(delta-input);
    public static long ScalarBinaryPreserved(int input,int delta)=>((long)input<<32)^(long)delta^(input<0?-7L:unchecked(input+delta));
    public static long ScalarBinaryTwice(int input,int delta)=>(input<0?-7L:unchecked(input+delta))+(delta==0?-9L:unchecked(input-delta));
    private static int? OptionalReturn(int input)=>input<0?null:input;
    public static long ReadOptionalPreserved(int input){int? value=OptionalOverflow(input);return ((long)input<<40)^value.GetValueOrDefault();}
    public static long ScalarOptionalPreserved(int input)=>((long)input<<40)^(input==0?0:unchecked(input+1));
    public static long ReadOptionalTwice(int input){int? left=OptionalReturn(input);int? right=OptionalOverflow(input);return (long)left.GetValueOrDefault(-7)+right.GetValueOrDefault(-9);}
    public static long ScalarOptionalTwice(int input)=>(long)(input<0?-7:input)+(input==0?-9:unchecked(input+1));
    private static int? OptionalOverflow(int input)=>input==0?null:unchecked(input+1);
    public static long ReadOptionalReturn(int input){int? value=OptionalReturn(input);return value.GetValueOrDefault();}
    public static long ReadOptionalOverflow(int input){int? value=OptionalOverflow(input);return value.GetValueOrDefault();}
    public static long ScalarOptionalReturn(int input)=>input<0?0L:input;
    public static long ScalarOptionalOverflow(int input)=>input==0?0L:unchecked(input+1);
    public static long ScalarOptionalPacked(int input){if(input>=0)return (((long)input<<32)>>>31)|1L;return 0L;}
    public static long ScalarOverflowPacked(int input){if(input!=0)return (((long)unchecked(input+1)<<32)>>>31)|1L;return 0L;}
    private static long ConsumeScaledAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceScaled(payload);}return unchecked(payload+payload); }
    public static long ReadScaledAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeScaledAdvance(value,fallback); }
    private static long ConsumeHighScaledAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=unchecked((long)value.Value+long.MinValue);for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceScaled(payload);}return payload; }
    public static long ReadHighScaledAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeHighScaledAdvance(value,fallback); }
    private static long ConsumeScaledOrderedAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceScaledOrdered(payload);}return unchecked(payload+payload); }
    public static long ReadScaledOrderedAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeScaledOrderedAdvance(value,fallback); }
    public static long ScalarScaledOrderedAdvance(long packed,int fallback) { long initial=(int)(packed>>>1);long after=fallback>0?unchecked(initial+fallback):unchecked(7625597484987L*initial+3812798742493L);return (packed&1L)!=0?(fallback>0?after:unchecked(2*after)):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback); }
    private static long ConsumeHighScaledOrderedAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=unchecked((long)value.Value+long.MinValue);for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceScaledOrdered(payload);}return payload; }
    public static long ReadHighScaledOrderedAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeHighScaledOrderedAdvance(value,fallback); }
    public static long ScalarHighScaledOrderedAdvance(long packed,int fallback) { long initial=unchecked((long)(int)(packed>>>1)+long.MinValue);long after=fallback>0?unchecked(initial+fallback):unchecked(7625597484987L*initial+3812798742493L);return (packed&1L)!=0?after:(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback); }
    public static long ScalarScaledAdvance(long packed,int fallback) { long initial=(int)(packed>>>1);long after=fallback>0?unchecked(initial+fallback):unchecked(initial+27L);return (packed&1L)!=0?(fallback>0?after:unchecked(2*after)):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback); }
    public static long ScalarHighScaledAdvance(long packed,int fallback) { long initial=unchecked((long)(int)(packed>>>1)+long.MinValue);long after=fallback>0?unchecked(initial+fallback):unchecked(initial+27L);return (packed&1L)!=0?after:(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback); }
    private static long ConsumeAffineAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceAffine(payload);}return unchecked(payload+payload); }
    public static long ReadAffineAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeAffineAdvance(value,fallback); }
    private static long ConsumeHighAffineAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=unchecked((long)value.Value+long.MinValue);for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceAffine(payload);}return payload; }
    public static long ReadHighAffineAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeHighAffineAdvance(value,fallback); }
    private static long ConsumeAffineOrderedAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceAffineOrdered(payload);}return unchecked(payload+payload); }
    public static long ReadAffineOrderedAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeAffineOrderedAdvance(value,fallback); }
    public static long ScalarAffineOrderedAdvance(long packed,int fallback) { long initial=(int)(packed>>>1);long after=fallback>0?unchecked(initial+fallback):unchecked(387420489L*initial+193710244L);return (packed&1L)!=0?(fallback>0?after:unchecked(2*after)):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback); }
    private static long ConsumeHighAffineOrderedAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=unchecked((long)value.Value+long.MinValue);for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceAffineOrdered(payload);}return payload; }
    public static long ReadHighAffineOrderedAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeHighAffineOrderedAdvance(value,fallback); }
    public static long ScalarHighAffineOrderedAdvance(long packed,int fallback) { long initial=unchecked((long)(int)(packed>>>1)+long.MinValue);long after=fallback>0?unchecked(initial+fallback):unchecked(387420489L*initial+193710244L);return (packed&1L)!=0?after:(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback); }
    public static long ScalarAffineAdvance(long packed,int fallback) { long initial=(int)(packed>>>1);long after=fallback>0?unchecked(initial+fallback):unchecked(initial+18L);return (packed&1L)!=0?(fallback>0?after:unchecked(2*after)):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback); }
    public static long ScalarHighAffineAdvance(long packed,int fallback) { long initial=unchecked((long)(int)(packed>>>1)+long.MinValue);long after=fallback>0?unchecked(initial+fallback):unchecked(initial+18L);return (packed&1L)!=0?after:(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback); }
    private static long ConsumeDependentAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceDependent(payload);}return unchecked(payload+payload); }
    public static long ReadDependentAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeDependentAdvance(value,fallback); }
    private static long ConsumeHighDependentAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=unchecked((long)value.Value+long.MinValue);for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceDependent(payload);}return payload; }
    public static long ReadHighDependentAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeHighDependentAdvance(value,fallback); }
    private static long ConsumeDependentOrderedAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceDependentOrdered(payload);}return unchecked(payload+payload); }
    public static long ReadDependentOrderedAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeDependentOrderedAdvance(value,fallback); }
    public static long ScalarDependentOrderedAdvance(long packed,int fallback) { long initial=(int)(packed>>>1);long after=fallback>0?unchecked(initial+fallback):unchecked(19683L*initial+9841L);return (packed&1L)!=0?(fallback>0?after:unchecked(2*after)):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback); }
    private static long ConsumeHighDependentOrderedAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=unchecked((long)value.Value+long.MinValue);for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceDependentOrdered(payload);}return payload; }
    public static long ReadHighDependentOrderedAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeHighDependentOrderedAdvance(value,fallback); }
    public static long ScalarHighDependentOrderedAdvance(long packed,int fallback) { long initial=unchecked((long)(int)(packed>>>1)+long.MinValue);long after=fallback>0?unchecked(initial+fallback):unchecked(19683L*initial+9841L);return (packed&1L)!=0?after:(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback); }
    public static long ScalarDependentAdvance(long packed,int fallback) { long initial=(int)(packed>>>1);long after=fallback>0?unchecked(initial+fallback):unchecked(initial+9L);return (packed&1L)!=0?(fallback>0?after:unchecked(2*after)):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback); }
    public static long ScalarHighDependentAdvance(long packed,int fallback) { long initial=unchecked((long)(int)(packed>>>1)+long.MinValue);long after=fallback>0?unchecked(initial+fallback):unchecked(initial+9L);return (packed&1L)!=0?after:(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback); }
    public static long Read(long input) { long? value = input < 0 ? null : input; return value.Value; }
    private static long ConsumeParameter(int? value)=>(long)value.GetValueOrDefault();
    public static long ReadParameter(int input) { int? value=input;return ConsumeParameter(value); }
    public static long PackedParameter(long packed)=>(long)(int)(packed>>>1);
    private static long ConsumePresence(int? value)=>value.HasValue?1L:0L;
    public static long ReadPresence(int input) { int? value=input;return ConsumePresence(value); }
    public static long PackedPresence(long packed)=>(packed&1L)!=0?1L:0L;
    private static long ConsumeDefault(int? value)=>(long)value.GetValueOrDefault(-7);
    public static long ReadDefault(int input) { int? value=input;return ConsumeDefault(value); }
    public static long PackedDefault(long packed)=>(packed&1L)!=0?(long)(int)(packed>>>1):-7L;
    private static long ConsumeDynamic(int? value,int fallback)=>(long)value.GetValueOrDefault(fallback);
    public static long ReadDynamic(int input,int fallback) { int? value=input;return ConsumeDynamic(value,fallback); }
    public static long PackedDynamic(long packed,int fallback)=>(packed&1L)!=0?(long)(int)(packed>>>1):(long)fallback;
    private static long ConsumeComputed(int? value,int fallback)=>(long)value.GetValueOrDefault(unchecked(fallback+1));
    public static long ReadComputed(int input,int fallback) { int? value=input;return ConsumeComputed(value,fallback); }
    public static long PackedComputed(long packed,int fallback)=>(packed&1L)!=0?(long)(int)(packed>>>1):(long)unchecked(fallback+1);
    private static long ConsumeComputedConditional(int? value,int fallback)=>(long)value.GetValueOrDefault(fallback==0?-7:unchecked(fallback+1));
    public static long ReadComputedConditional(int input,int fallback) { int? value=input;return ConsumeComputedConditional(value,fallback); }
    public static long PackedComputedConditional(long packed,int fallback)=>(packed&1L)!=0?(long)(int)(packed>>>1):(long)(fallback==0?-7:unchecked(fallback+1));
    private static int AdjustHelper(int fallback)=>unchecked(fallback+1);
    private static long ConsumeExternalHelper(int? value,int fallback)=>(long)value.GetValueOrDefault(ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback));
    private static long ConsumeConstantHelper(int? value)=>(long)value.GetValueOrDefault(ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(-7));
    private static long ConsumeBinaryHelper(int? value)=>(long)value.GetValueOrDefault(ManagedPortSmoke.Dependency.MetadataOnlyCallee.CombineFallback(-7,1));
    private static long ConsumeGenericHelper(int? value)=>(long)value.GetValueOrDefault(ManagedPortSmoke.Dependency.MetadataOnlyCallee.FallbackIdentity<int>(-7));
    private static long ConsumeOwnerHelper(int? value)=>(long)value.GetValueOrDefault(ManagedPortSmoke.Dependency.FallbackOwner<int>.Identity(-7));
    private static long ConsumeCombinedHelper(int? value)=>(long)value.GetValueOrDefault(ManagedPortSmoke.Dependency.FallbackOwner<int>.MethodIdentity<int>(-7));
    private static long ConsumeLazyHelper(int? value)=>(long)(value ?? ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(-7));
    private static long ConsumeExplicitPresence(int? value,int fallback)=>(long)(value.HasValue?value.GetValueOrDefault():ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback));
    private static long ConsumeGuardedValue(int? value,int fallback)=>(long)(value.HasValue?value.Value:ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback));
    private static long ConsumeEarlyReturn(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);return value.Value; }
    private static long ConsumePresentFirst(int? value,int fallback) { if(value.HasValue)return value.Value;return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback); }
    private static long ConsumePayloadAdd(int? value,int fallback) { if(value.HasValue)return unchecked(value.Value+fallback);return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback); }
    private static long ConsumeTrailingAdd(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);return unchecked(value.Value+fallback); }
    private static long ConsumeMergedAdd(int? value,int fallback)=>(long)(value.HasValue?unchecked(value.Value+fallback):ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback));
    private static long ConsumeRepeatTrailing(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);return unchecked(value.Value+value.Value); }
    private static long ConsumeLocalTrailing(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);int payload=value.Value;if(fallback>0)payload=unchecked(payload+fallback);return unchecked(payload+payload); }
    private static long ConsumeCounted3(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);int payload=value.Value;for(int i=0;i<3;i++)payload=unchecked(payload+fallback);return unchecked(payload+payload); }
    private static long ConsumeCountedGetter3(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);int payload=value.Value;for(int i=0;i<3;i++)payload=unchecked(payload+value.Value);return unchecked(payload+payload); }
    private static long ConsumeLoopIf(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);int payload=value.Value;for(int i=0;i<3;i++)if(fallback>0)payload=unchecked(payload+value.Value);return unchecked(payload+payload); }
    private static long ConsumeLoopBreak(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);int payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)break;payload=unchecked(payload+value.Value);}return unchecked(payload+payload); }
    private static long ConsumeLoopReturn(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);int payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return payload;payload=unchecked(payload+value.Value);}return unchecked(payload+payload); }
    private static long ConsumeWideReturn(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);int payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return (long)payload+fallback;payload=unchecked(payload+value.Value);}return unchecked(payload+payload); }
    private static long ConsumeI8LocalReturn(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=unchecked(payload+value.Value);}return unchecked(payload+payload); }
    private static long ConsumePresentI8LocalReturn(int? value,int fallback) { if(value.HasValue){long payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=unchecked(payload+value.Value);}return unchecked(payload+payload);}return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback); }
    private static long ConsumeCallI8LocalReturn(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=unchecked(payload+ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(value.Value));}return unchecked(payload+payload); }
    private static long ConsumePresentCallI8LocalReturn(int? value,int fallback) { if(value.HasValue){long payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=unchecked(payload+ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(value.Value));}return unchecked(payload+payload);}return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback); }
    private static long ConsumeWideHelper(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvancePayload(payload);}return unchecked(payload+payload); }
    private static long ConsumeWideMethodHelper(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.FallbackIdentity<long>(unchecked(payload+fallback));}return unchecked(payload+payload); }
    private static long ConsumeMethodChoose(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.FallbackChoose<long>(i,payload,unchecked(payload+fallback));}return unchecked(payload+payload); }
    private static long ConsumeHighMethodChoose(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=unchecked((long)value.Value+long.MinValue);for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.FallbackChoose<long>(i,payload,unchecked(payload+((long)fallback<<40)));}return payload; }
    private static long ConsumeOwnerChoose(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.FallbackOwner<long>.Choose(i,payload,unchecked(payload+fallback));}return unchecked(payload+payload); }
    private static long ConsumeHighOwnerChoose(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=unchecked((long)value.Value+long.MinValue);for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.FallbackOwner<long>.Choose(i,payload,unchecked(payload+((long)fallback<<40)));}return payload; }
    private static long ConsumeCombinedChoose(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.FallbackOwner<long>.MethodChoose<long>(i,payload,unchecked(payload+fallback));}return unchecked(payload+payload); }
    private static long ConsumeHighCombinedChoose(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=unchecked((long)value.Value+long.MinValue);for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.FallbackOwner<long>.MethodChoose<long>(i,payload,unchecked(payload+((long)fallback<<40)));}return payload; }
    private static long ConsumeMixedAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceMixed(payload,fallback);}return unchecked(payload+payload); }
    private static long ConsumeCountedAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceCounted(payload);}return unchecked(payload+payload); }
    private static long ConsumeBoundedAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceBounded(payload,2);}return unchecked(payload+payload); }
    private static long ConsumeHighBoundedAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=unchecked((long)value.Value+long.MinValue);for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceBounded(payload,2);}return payload; }
    public static long ReadBoundedAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeBoundedAdvance(value,fallback); }
    private static long ConsumeStrideAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceStride(payload);}return unchecked(payload+payload); }
    private static long ConsumeHighStrideAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=unchecked((long)value.Value+long.MinValue);for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceStride(payload);}return payload; }
    public static long ReadStrideAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeStrideAdvance(value,fallback); }
    private static long ConsumeRoundedAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceStrideRounded(payload);}return unchecked(payload+payload); }
    private static long ConsumeHighRoundedAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=unchecked((long)value.Value+long.MinValue);for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceStrideRounded(payload);}return payload; }
    public static long ReadRoundedAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeRoundedAdvance(value,fallback); }
    private static long ConsumeSeedAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceSeed(payload);}return unchecked(payload+payload); }
    private static long ConsumeHighSeedAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=unchecked((long)value.Value+long.MinValue);for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceSeed(payload);}return payload; }
    private static long ConsumeNegativeSeedAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceNegativeSeed(payload);}return unchecked(payload+payload); }
    private static long ConsumeHighNegativeSeedAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=unchecked((long)value.Value+long.MinValue);for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceNegativeSeed(payload);}return payload; }
    public static long ReadSeedAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeSeedAdvance(value,fallback); }
    private static long ConsumeDescendingAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceDescending(payload);}return unchecked(payload+payload); }
    private static long ConsumeHighDescendingAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=unchecked((long)value.Value+long.MinValue);for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceDescending(payload);}return payload; }
    private static long ConsumeDescendingRoundedAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceDescendingRounded(payload);}return unchecked(payload+payload); }
    private static long ConsumeHighDescendingRoundedAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=unchecked((long)value.Value+long.MinValue);for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceDescendingRounded(payload);}return payload; }
    public static long ReadDescendingAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeDescendingAdvance(value,fallback); }
    private static long ConsumeInclusiveAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceInclusive(payload);}return unchecked(payload+payload); }
    private static long ConsumeHighInclusiveAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=unchecked((long)value.Value+long.MinValue);for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceInclusive(payload);}return payload; }
    private static long ConsumeInclusiveAscendingAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceInclusiveAscending(payload);}return unchecked(payload+payload); }
    private static long ConsumeHighInclusiveAscendingAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=unchecked((long)value.Value+long.MinValue);for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceInclusiveAscending(payload);}return payload; }
    public static long ReadInclusiveAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeInclusiveAdvance(value,fallback); }
    private static long ConsumeUnsignedAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceUnsigned(payload);}return unchecked(payload+payload); }
    private static long ConsumeHighUnsignedAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=unchecked((long)value.Value+long.MinValue);for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceUnsigned(payload);}return payload; }
    private static long ConsumeUnsignedHighCounterAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceUnsignedHigh(payload);}return unchecked(payload+payload); }
    private static long ConsumeHighUnsignedHighCounterAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=unchecked((long)value.Value+long.MinValue);for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceUnsignedHigh(payload);}return payload; }
    public static long ReadUnsignedAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeUnsignedAdvance(value,fallback); }
    private static long ConsumeUnsigned64Advance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceUnsigned64(payload);}return unchecked(payload+payload); }
    private static long ConsumeHighUnsigned64Advance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=unchecked((long)value.Value+long.MinValue);for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceUnsigned64(payload);}return payload; }
    private static long ConsumeUnsigned64HighCounterAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceUnsigned64High(payload);}return unchecked(payload+payload); }
    private static long ConsumeHighUnsigned64HighCounterAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=unchecked((long)value.Value+long.MinValue);for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceUnsigned64High(payload);}return payload; }
    public static long ReadUnsigned64Advance(int input,int fallback) { int? value=input<0?null:input;return ConsumeUnsigned64Advance(value,fallback); }
    private static long ConsumeSigned64Advance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceSigned64(payload);}return unchecked(payload+payload); }
    public static long ReadSigned64Advance(int input,int fallback) { int? value=input<0?null:input;return ConsumeSigned64Advance(value,fallback); }
    private static long ConsumeSequentialAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceSequential(payload);}return unchecked(payload+payload); }
    public static long ReadSequentialAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeSequentialAdvance(value,fallback); }
    private static long ConsumeNestedAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceNested(payload);}return unchecked(payload+payload); }
    public static long ReadNestedAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeNestedAdvance(value,fallback); }
    private static long ConsumeHighNestedAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=unchecked((long)value.Value+long.MinValue);for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceNested(payload);}return payload; }
    public static long ReadHighNestedAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeHighNestedAdvance(value,fallback); }
    private static long ConsumeNestedOrderedAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceNestedOrdered(payload);}return unchecked(payload+payload); }
    public static long ReadNestedOrderedAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeNestedOrderedAdvance(value,fallback); }
    public static long ScalarNestedOrderedAdvance(long packed,int fallback) { long initial=(int)(packed>>>1);long after=fallback>0?unchecked(initial+fallback):unchecked(531441L*initial+265720L);return (packed&1L)!=0?(fallback>0?after:unchecked(2*after)):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback); }
    private static long ConsumeHighNestedOrderedAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=unchecked((long)value.Value+long.MinValue);for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceNestedOrdered(payload);}return payload; }
    public static long ReadHighNestedOrderedAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeHighNestedOrderedAdvance(value,fallback); }
    public static long ScalarHighNestedOrderedAdvance(long packed,int fallback) { long initial=unchecked((long)(int)(packed>>>1)+long.MinValue);long after=fallback>0?unchecked(initial+fallback):unchecked(531441L*initial+265720L);return (packed&1L)!=0?after:(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback); }
    public static long ScalarSequentialAdvance(long packed,int fallback) { long initial=(int)(packed>>>1);long after=fallback>0?unchecked(initial+fallback):unchecked(initial+12L);return (packed&1L)!=0?(fallback>0?after:unchecked(2*after)):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback); }
    private static long ConsumeHighSequentialAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=unchecked((long)value.Value+long.MinValue);for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceSequential(payload);}return payload; }
    public static long ReadHighSequentialAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeHighSequentialAdvance(value,fallback); }
    public static long ScalarHighSequentialAdvance(long packed,int fallback) { long initial=unchecked((long)(int)(packed>>>1)+long.MinValue);long after=fallback>0?unchecked(initial+fallback):unchecked(initial+12L);return (packed&1L)!=0?after:(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback); }
    private static long ConsumeSequentialOrderedAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceSequentialOrdered(payload);}return unchecked(payload+payload); }
    public static long ReadSequentialOrderedAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeSequentialOrderedAdvance(value,fallback); }
    public static long ScalarSequentialOrderedAdvance(long packed,int fallback) { long initial=(int)(packed>>>1);long after=fallback>0?unchecked(initial+fallback):unchecked(27L*initial+104L);return (packed&1L)!=0?(fallback>0?after:unchecked(2*after)):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback); }
    private static long ConsumeHighSequentialOrderedAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=unchecked((long)value.Value+long.MinValue);for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceSequentialOrdered(payload);}return payload; }
    public static long ReadHighSequentialOrderedAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeHighSequentialOrderedAdvance(value,fallback); }
    public static long ScalarHighSequentialOrderedAdvance(long packed,int fallback) { long initial=unchecked((long)(int)(packed>>>1)+long.MinValue);long after=fallback>0?unchecked(initial+fallback):unchecked(27L*initial+104L);return (packed&1L)!=0?after:(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback); }
    private static long ConsumeHighSigned64Advance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=unchecked((long)value.Value+long.MinValue);for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceSigned64(payload);}return payload; }
    public static long ReadHighSigned64Advance(int input,int fallback) { int? value=input<0?null:input;return ConsumeHighSigned64Advance(value,fallback); }
    private static long ConsumeSigned64NegativeAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceSigned64Negative(payload);}return unchecked(payload+payload); }
    public static long ReadSigned64NegativeAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeSigned64NegativeAdvance(value,fallback); }
    private static long ConsumeHighSigned64NegativeAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=unchecked((long)value.Value+long.MinValue);for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceSigned64Negative(payload);}return payload; }
    public static long ReadHighSigned64NegativeAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeHighSigned64NegativeAdvance(value,fallback); }
    private static long ConsumeSigned64BoundAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceSigned64Bound(payload,3L);}return unchecked(payload+payload); }
    public static long ReadSigned64BoundAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeSigned64BoundAdvance(value,fallback); }
    private static long ConsumeHighSigned64BoundAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=unchecked((long)value.Value+long.MinValue);for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceSigned64Bound(payload,3L);}return payload; }
    public static long ReadHighSigned64BoundAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeHighSigned64BoundAdvance(value,fallback); }
    public static long ReadHighUnsigned64Advance(int input,int fallback) { int? value=input<0?null:input;return ConsumeHighUnsigned64Advance(value,fallback); }
    public static long ReadUnsigned64HighCounterAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeUnsigned64HighCounterAdvance(value,fallback); }
    public static long ReadHighUnsigned64HighCounterAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeHighUnsigned64HighCounterAdvance(value,fallback); }
    public static long ReadHighUnsignedAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeHighUnsignedAdvance(value,fallback); }
    public static long ReadUnsignedHighCounterAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeUnsignedHighCounterAdvance(value,fallback); }
    public static long ReadHighUnsignedHighCounterAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeHighUnsignedHighCounterAdvance(value,fallback); }
    public static long ReadHighInclusiveAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeHighInclusiveAdvance(value,fallback); }
    public static long ReadInclusiveAscendingAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeInclusiveAscendingAdvance(value,fallback); }
    public static long ReadHighInclusiveAscendingAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeHighInclusiveAscendingAdvance(value,fallback); }
    public static long ReadHighDescendingAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeHighDescendingAdvance(value,fallback); }
    public static long ReadDescendingRoundedAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeDescendingRoundedAdvance(value,fallback); }
    public static long ReadHighDescendingRoundedAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeHighDescendingRoundedAdvance(value,fallback); }
    public static long ReadHighSeedAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeHighSeedAdvance(value,fallback); }
    public static long ReadNegativeSeedAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeNegativeSeedAdvance(value,fallback); }
    public static long ReadHighNegativeSeedAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeHighNegativeSeedAdvance(value,fallback); }
    public static long ScalarNegativeSeedAdvance(long packed,int fallback)=>(packed&1L)!=0?unchecked(fallback>0?(long)(int)(packed>>>1)+fallback:2*((long)(int)(packed>>>1)+9L)):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);
    public static long ScalarHighNegativeSeedAdvance(long packed,int fallback)=>(packed&1L)!=0?unchecked((long)(int)(packed>>>1)+long.MinValue+(fallback>0?fallback:9L)):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);
    public static long ReadHighRoundedAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeHighRoundedAdvance(value,fallback); }
    public static long ReadHighStrideAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeHighStrideAdvance(value,fallback); }
    public static long ReadHighBoundedAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeHighBoundedAdvance(value,fallback); }
    private static long ConsumeHighCountedAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=unchecked((long)value.Value+long.MinValue);for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceCounted(payload);}return payload; }
    public static long ReadCountedAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeCountedAdvance(value,fallback); }
    public static long ReadHighCountedAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeHighCountedAdvance(value,fallback); }
    public static long ScalarCountedAdvance(long packed,int fallback)=>(packed&1L)!=0?unchecked(fallback>0?(long)(int)(packed>>>1)+fallback:2*((long)(int)(packed>>>1)+6L)):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);
    public static long ScalarHighCountedAdvance(long packed,int fallback)=>(packed&1L)!=0?unchecked((long)(int)(packed>>>1)+long.MinValue+(fallback>0?fallback:6L)):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);
    private static long ConsumeHighMixedAdvance(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=unchecked((long)value.Value+long.MinValue);for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvanceMixed(payload,fallback);}return payload; }
    public static long ReadMethodChoose(int input,int fallback) { int? value=input<0?null:input;return ConsumeMethodChoose(value,fallback); }
    public static long ReadHighMethodChoose(int input,int fallback) { int? value=input<0?null:input;return ConsumeHighMethodChoose(value,fallback); }
    public static long ReadOwnerChoose(int input,int fallback) { int? value=input<0?null:input;return ConsumeOwnerChoose(value,fallback); }
    public static long ReadHighOwnerChoose(int input,int fallback) { int? value=input<0?null:input;return ConsumeHighOwnerChoose(value,fallback); }
    public static long ReadCombinedChoose(int input,int fallback) { int? value=input<0?null:input;return ConsumeCombinedChoose(value,fallback); }
    public static long ReadHighCombinedChoose(int input,int fallback) { int? value=input<0?null:input;return ConsumeHighCombinedChoose(value,fallback); }
    public static long ReadMixedAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeMixedAdvance(value,fallback); }
    public static long ReadHighMixedAdvance(int input,int fallback) { int? value=input<0?null:input;return ConsumeHighMixedAdvance(value,fallback); }
    public static long ScalarChoose(long packed,int fallback)=>(packed&1L)!=0?fallback>0?(long)(int)(packed>>>1)+fallback:2L*((long)(int)(packed>>>1)+2L*fallback):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);
    public static long ScalarHighChoose(long packed,int fallback)=>(packed&1L)!=0?unchecked((long)(int)(packed>>>1)+long.MinValue+(fallback>0?fallback:2L*fallback*1099511627776L)):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);
    public static long ScalarHighMixedAdvance(long packed,int fallback)=>(packed&1L)!=0?unchecked((long)(int)(packed>>>1)+long.MinValue+(fallback>0?fallback:3L*fallback)):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);
    private static long ConsumeWideMethodNext(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.FallbackNext<long>(payload,unchecked(payload+fallback));}return unchecked(payload+payload); }
    private static long ConsumeHighWideMethodNext(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=unchecked((long)value.Value+long.MinValue);for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.FallbackNext<long>(payload,unchecked(payload+((long)fallback<<40)));}return payload; }
    private static long ConsumeWideOwnerNext(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.FallbackOwner<long>.Next(payload,unchecked(payload+fallback));}return unchecked(payload+payload); }
    private static long ConsumeHighWideOwnerNext(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=unchecked((long)value.Value+long.MinValue);for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.FallbackOwner<long>.Next(payload,unchecked(payload+((long)fallback<<40)));}return payload; }
    private static long ConsumeWideCombinedNext(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.FallbackOwner<long>.MethodNext<long>(payload,unchecked(payload+fallback));}return unchecked(payload+payload); }
    private static long ConsumeHighWideCombinedNext(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=unchecked((long)value.Value+long.MinValue);for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.FallbackOwner<long>.MethodNext<long>(payload,unchecked(payload+((long)fallback<<40)));}return payload; }
    public static long ReadWideMethodNext(int input,int fallback) { int? value=input<0?null:input;return ConsumeWideMethodNext(value,fallback); }
    public static long ReadHighWideMethodNext(int input,int fallback) { int? value=input<0?null:input;return ConsumeHighWideMethodNext(value,fallback); }
    public static long ReadWideOwnerNext(int input,int fallback) { int? value=input<0?null:input;return ConsumeWideOwnerNext(value,fallback); }
    public static long ReadHighWideOwnerNext(int input,int fallback) { int? value=input<0?null:input;return ConsumeHighWideOwnerNext(value,fallback); }
    public static long ReadWideCombinedNext(int input,int fallback) { int? value=input<0?null:input;return ConsumeWideCombinedNext(value,fallback); }
    public static long ReadHighWideCombinedNext(int input,int fallback) { int? value=input<0?null:input;return ConsumeHighWideCombinedNext(value,fallback); }
    private static long ConsumeWideOwnerHelper(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.FallbackOwner<long>.Identity(unchecked(payload+fallback));}return unchecked(payload+payload); }
    private static long ConsumeHighWideOwnerHelper(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=unchecked((long)value.Value+long.MinValue);for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.FallbackOwner<long>.Identity(unchecked(payload+((long)fallback<<40)));}return payload; }
    private static long ConsumeWideCombinedHelper(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.FallbackOwner<long>.MethodIdentity<long>(unchecked(payload+fallback));}return unchecked(payload+payload); }
    private static long ConsumeHighWideCombinedHelper(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=unchecked((long)value.Value+long.MinValue);for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.FallbackOwner<long>.MethodIdentity<long>(unchecked(payload+((long)fallback<<40)));}return payload; }
    public static long ReadWideOwnerHelper(int input,int fallback) { int? value=input<0?null:input;return ConsumeWideOwnerHelper(value,fallback); }
    public static long ReadHighWideOwnerHelper(int input,int fallback) { int? value=input<0?null:input;return ConsumeHighWideOwnerHelper(value,fallback); }
    public static long ReadWideCombinedHelper(int input,int fallback) { int? value=input<0?null:input;return ConsumeWideCombinedHelper(value,fallback); }
    public static long ReadHighWideCombinedHelper(int input,int fallback) { int? value=input<0?null:input;return ConsumeHighWideCombinedHelper(value,fallback); }
    private static long ConsumeHighWideMethodHelper(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=unchecked((long)value.Value+long.MinValue);for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.FallbackIdentity<long>(unchecked(payload+((long)fallback<<40)));}return payload; }
    public static long ReadWideMethodHelper(int input,int fallback) { int? value=input<0?null:input;return ConsumeWideMethodHelper(value,fallback); }
    public static long ReadHighWideMethodHelper(int input,int fallback) { int? value=input<0?null:input;return ConsumeHighWideMethodHelper(value,fallback); }
    public static long ScalarHighWideMethodHelper(long packed,int fallback)=>(packed&1L)!=0?unchecked((long)(int)(packed>>>1)+long.MinValue+(fallback>0?fallback:3L*fallback*1099511627776L)):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);
    private static long ConsumeWideBinaryHelper(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvancePayloadBy(payload,(long)fallback);}return unchecked(payload+payload); }
    private static long ConsumeHighWideBinaryHelper(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=unchecked((long)value.Value+long.MinValue);for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvancePayloadBy(payload,(long)fallback<<40);}return payload; }
    public static long ReadWideBinaryHelper(int input,int fallback) { int? value=input<0?null:input;return ConsumeWideBinaryHelper(value,fallback); }
    public static long ReadHighWideBinaryHelper(int input,int fallback) { int? value=input<0?null:input;return ConsumeHighWideBinaryHelper(value,fallback); }
    public static long ScalarWideBinaryHelper(long packed,int fallback)=>(packed&1L)!=0?fallback>0?(long)(int)(packed>>>1)+fallback:2L*((long)(int)(packed>>>1)+3L*fallback):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);
    public static long ScalarHighWideBinaryHelper(long packed,int fallback)=>(packed&1L)!=0?unchecked((long)(int)(packed>>>1)+long.MinValue+(fallback>0?fallback:3L*fallback*1099511627776L)):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);
    private static long ConsumeHighWideHelper(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=unchecked((long)value.Value+long.MaxValue);for(int i=0;i<3;i++){if(fallback>0)return payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdvancePayload(payload);}return payload; }
    public static long ReadWideHelper(int input,int fallback) { int? value=input<0?null:input;return ConsumeWideHelper(value,fallback); }
    public static long ReadHighWideHelper(int input,int fallback) { int? value=input<0?null:input;return ConsumeHighWideHelper(value,fallback); }
    public static long ScalarWideHelper(long packed,int fallback)=>(packed&1L)!=0?fallback>0?(long)(int)(packed>>>1)+fallback:2L*((long)(int)(packed>>>1)+3):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);
    public static long ScalarHighWideHelper(long packed,int fallback)=>(packed&1L)!=0?unchecked((long)(int)(packed>>>1)+long.MaxValue+(fallback>0?fallback:3L)):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);
    private static long ConsumeIndexI8LocalReturn(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=value.Value;for(int i=0;i<3;i++){if(i>=fallback)return payload+fallback;payload=unchecked(payload+value.Value);}return unchecked(payload+payload); }
    private static long ConsumeCallIndexI8LocalReturn(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=value.Value;for(int i=0;i<3;i++){if(i>=fallback)return payload+fallback;payload=unchecked(payload+ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(value.Value));}return unchecked(payload+payload); }
    private static long ConsumeHighI8LocalReturn(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return (payload<<40)^fallback;payload=unchecked(payload+value.Value);}return unchecked(payload+payload); }
    private static long ConsumeCallIndexNormalI8LocalReturn(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=value.Value;for(int i=0;i<3;i++){if(i>=fallback)return payload+fallback;payload=unchecked(payload+ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(value.Value));}return payload+fallback; }
    private static long ConsumeI8LocalPhi(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=value.Value;for(int i=0;i<3;i++)payload=fallback>0?unchecked(payload+value.Value):payload^fallback;return unchecked(payload+payload); }
    private static long ConsumeCallI8LocalPhi(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);long payload=value.Value;for(int i=0;i<3;i++)payload=fallback>0?unchecked(payload+ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(value.Value)):payload^fallback;return unchecked(payload+payload); }
    public static long ReadI8LocalReturn(int input,int fallback) { int? value=input<0?null:input;return ConsumeI8LocalReturn(value,fallback); }
    public static long ReadPresentI8LocalReturn(int input,int fallback) { int? value=input<0?null:input;return ConsumePresentI8LocalReturn(value,fallback); }
    public static long ReadCallI8LocalReturn(int input,int fallback) { int? value=input<0?null:input;return ConsumeCallI8LocalReturn(value,fallback); }
    public static long ReadPresentCallI8LocalReturn(int input,int fallback) { int? value=input<0?null:input;return ConsumePresentCallI8LocalReturn(value,fallback); }
    public static long ReadIndexI8LocalReturn(int input,int fallback) { int? value=input<0?null:input;return ConsumeIndexI8LocalReturn(value,fallback); }
    public static long ReadCallIndexI8LocalReturn(int input,int fallback) { int? value=input<0?null:input;return ConsumeCallIndexI8LocalReturn(value,fallback); }
    public static long ReadHighI8LocalReturn(int input,int fallback) { int? value=input<0?null:input;return ConsumeHighI8LocalReturn(value,fallback); }
    public static long ReadCallIndexNormalI8LocalReturn(int input,int fallback) { int? value=input<0?null:input;return ConsumeCallIndexNormalI8LocalReturn(value,fallback); }
    public static long ReadI8LocalPhi(int input,int fallback) { int? value=input<0?null:input;return ConsumeI8LocalPhi(value,fallback); }
    public static long ReadCallI8LocalPhi(int input,int fallback) { int? value=input<0?null:input;return ConsumeCallI8LocalPhi(value,fallback); }
    public static long ScalarI8LocalReturn(long packed,int fallback)=>(packed&1L)!=0?fallback>0?(long)(int)(packed>>>1)+fallback:8L*(int)(packed>>>1):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);
    public static long ScalarCallI8LocalReturn(long packed,int fallback) { if((packed&1L)==0)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);int value=(int)(packed>>>1);return fallback>0?(long)value+fallback:2L*value+6L*unchecked(value+1); }
    public static long ScalarIndexI8LocalReturn(long packed,int fallback) { if((packed&1L)==0)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);int count=fallback<=0?0:fallback>=3?3:fallback;long result=(1L+count)*(int)(packed>>>1);return fallback<3?result+fallback:result*2; }
    public static long ScalarCallIndexI8LocalReturn(long packed,int fallback) { if((packed&1L)==0)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);int value=(int)(packed>>>1),count=fallback<=0?0:fallback>=3?3:fallback;long result=(long)value+(long)count*unchecked(value+1);return fallback<3?result+fallback:result*2; }
    public static long ScalarHighI8LocalReturn(long packed,int fallback)=>(packed&1L)!=0?fallback>0?unchecked((long)(int)(packed>>>1)*1099511627776L)^fallback:8L*(int)(packed>>>1):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);
    public static long ScalarCallIndexNormalI8LocalReturn(long packed,int fallback) { if((packed&1L)==0)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);int value=(int)(packed>>>1),count=fallback<=0?0:fallback>=3?3:fallback;return (long)value+(long)count*unchecked(value+1)+fallback; }
    public static long ScalarI8LocalPhi(long packed,int fallback)=>(packed&1L)!=0?fallback>0?8L*(int)(packed>>>1):2*((long)(int)(packed>>>1)^fallback):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);
    public static long ScalarCallI8LocalPhi(long packed,int fallback) { if((packed&1L)==0)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);int value=(int)(packed>>>1);return fallback>0?2L*value+6L*unchecked(value+1):2*((long)value^fallback); }
    private static long ConsumePresentWideReturn(int? value,int fallback) { if(value.HasValue){int payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return (long)payload+fallback;payload=unchecked(payload+value.Value);}return unchecked(payload+payload);}return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback); }
    private static long ConsumeCallWideReturn(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);int payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return (long)payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(unchecked(payload+value.Value));}return unchecked(payload+payload); }
    private static long ConsumePresentCallWideReturn(int? value,int fallback) { if(value.HasValue){int payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return (long)payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(unchecked(payload+value.Value));}return unchecked(payload+payload);}return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback); }
    private static long ConsumeIndexWideReturn(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);int payload=value.Value;for(int i=0;i<3;i++){if(i>=fallback)return (long)payload+fallback;payload=unchecked(payload+value.Value);}return unchecked(payload+payload); }
    private static long ConsumeCallIndexWideReturn(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);int payload=value.Value;for(int i=0;i<3;i++){if(i>=fallback)return (long)payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(unchecked(payload+value.Value));}return unchecked(payload+payload); }
    private static long ConsumeHighReturn(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);int payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return ((long)payload<<40)^fallback;payload=unchecked(payload+value.Value);}return unchecked(payload+payload); }
    private static long ConsumeCallIndexHighReturn(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);int payload=value.Value;for(int i=0;i<3;i++){if(i>=fallback)return ((long)payload<<40)^fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(unchecked(payload+value.Value));}return unchecked(payload+payload); }
    private static long ConsumeNormalWideReturn(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);int payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return (long)payload+fallback;payload=unchecked(payload+value.Value);}return (long)payload+fallback; }
    private static long ConsumeCallIndexNormalWideReturn(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);int payload=value.Value;for(int i=0;i<3;i++){if(i>=fallback)return (long)payload+fallback;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(unchecked(payload+value.Value));}return (long)payload+fallback; }
    public static long ReadWideReturn(int input,int fallback) { int? value=input<0?null:input;return ConsumeWideReturn(value,fallback); }
    public static long ReadPresentWideReturn(int input,int fallback) { int? value=input<0?null:input;return ConsumePresentWideReturn(value,fallback); }
    public static long ReadCallWideReturn(int input,int fallback) { int? value=input<0?null:input;return ConsumeCallWideReturn(value,fallback); }
    public static long ReadPresentCallWideReturn(int input,int fallback) { int? value=input<0?null:input;return ConsumePresentCallWideReturn(value,fallback); }
    public static long ReadIndexWideReturn(int input,int fallback) { int? value=input<0?null:input;return ConsumeIndexWideReturn(value,fallback); }
    public static long ReadCallIndexWideReturn(int input,int fallback) { int? value=input<0?null:input;return ConsumeCallIndexWideReturn(value,fallback); }
    public static long ReadHighReturn(int input,int fallback) { int? value=input<0?null:input;return ConsumeHighReturn(value,fallback); }
    public static long ReadCallIndexHighReturn(int input,int fallback) { int? value=input<0?null:input;return ConsumeCallIndexHighReturn(value,fallback); }
    public static long ReadNormalWideReturn(int input,int fallback) { int? value=input<0?null:input;return ConsumeNormalWideReturn(value,fallback); }
    public static long ReadCallIndexNormalWideReturn(int input,int fallback) { int? value=input<0?null:input;return ConsumeCallIndexNormalWideReturn(value,fallback); }
    public static long ScalarWideReturn(long packed,int fallback)=>(packed&1L)!=0?fallback>0?(long)(int)(packed>>>1)+fallback:(long)unchecked(8*(int)(packed>>>1)):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);
    public static long ScalarCallWideReturn(long packed,int fallback)=>(packed&1L)!=0?fallback>0?(long)(int)(packed>>>1)+fallback:(long)unchecked(8*(int)(packed>>>1)+6):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);
    public static long ScalarIndexWideReturn(long packed,int fallback) { if((packed&1L)==0)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);int count=fallback<=0?0:fallback>=3?3:fallback;int result=unchecked((1+count)*(int)(packed>>>1));return fallback<3?(long)result+fallback:(long)unchecked(result+result); }
    public static long ScalarCallIndexWideReturn(long packed,int fallback) { if((packed&1L)==0)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);int count=fallback<=0?0:fallback>=3?3:fallback;int result=unchecked((1+count)*(int)(packed>>>1)+count);return fallback<3?(long)result+fallback:(long)unchecked(result+result); }
    public static long ScalarHighReturn(long packed,int fallback)=>(packed&1L)!=0?fallback>0?unchecked((long)(int)(packed>>>1)*1099511627776L)^fallback:(long)unchecked(8*(int)(packed>>>1)):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);
    public static long ScalarCallIndexHighReturn(long packed,int fallback) { if((packed&1L)==0)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);int count=fallback<=0?0:fallback>=3?3:fallback;int result=unchecked((1+count)*(int)(packed>>>1)+count);return fallback<3?unchecked((long)result*1099511627776L)^fallback:(long)unchecked(result+result); }
    public static long ScalarNormalWideReturn(long packed,int fallback)=>(packed&1L)!=0?(long)(fallback>0?(int)(packed>>>1):unchecked(4*(int)(packed>>>1)))+fallback:(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);
    public static long ScalarCallIndexNormalWideReturn(long packed,int fallback) { if((packed&1L)==0)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);int count=fallback<=0?0:fallback>=3?3:fallback;int result=unchecked((1+count)*(int)(packed>>>1)+count);return (long)result+fallback; }
    private static long ConsumeLoopPresentReturn(int? value,int fallback) { if(value.HasValue){int payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return payload;payload=unchecked(payload+value.Value);}return unchecked(payload+payload);}return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback); }
    private static long ConsumeLoopCallReturn(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);int payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return payload;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(unchecked(payload+value.Value));}return unchecked(payload+payload); }
    private static long ConsumeLoopPresentCallReturn(int? value,int fallback) { if(value.HasValue){int payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)return payload;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(unchecked(payload+value.Value));}return unchecked(payload+payload);}return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback); }
    private static long ConsumeLoopIndexReturn(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);int payload=value.Value;for(int i=0;i<3;i++){if(i>=fallback)return payload;payload=unchecked(payload+value.Value);}return unchecked(payload+payload); }
    private static long ConsumeLoopCallIndexReturn(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);int payload=value.Value;for(int i=0;i<3;i++){if(i>=fallback)return payload;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(unchecked(payload+value.Value));}return unchecked(payload+payload); }
    public static long ReadLoopReturn(int input,int fallback) { int? value=input<0?null:input;return ConsumeLoopReturn(value,fallback); }
    public static long ReadLoopPresentReturn(int input,int fallback) { int? value=input<0?null:input;return ConsumeLoopPresentReturn(value,fallback); }
    public static long ReadLoopCallReturn(int input,int fallback) { int? value=input<0?null:input;return ConsumeLoopCallReturn(value,fallback); }
    public static long ReadLoopPresentCallReturn(int input,int fallback) { int? value=input<0?null:input;return ConsumeLoopPresentCallReturn(value,fallback); }
    public static long ReadLoopIndexReturn(int input,int fallback) { int? value=input<0?null:input;return ConsumeLoopIndexReturn(value,fallback); }
    public static long ReadLoopCallIndexReturn(int input,int fallback) { int? value=input<0?null:input;return ConsumeLoopCallIndexReturn(value,fallback); }
    public static long ScalarLoopReturn(long packed,int fallback)=>(packed&1L)!=0?fallback>0?(long)(int)(packed>>>1):(long)unchecked(8*(int)(packed>>>1)):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);
    public static long ScalarLoopCallReturn(long packed,int fallback)=>(packed&1L)!=0?fallback>0?(long)(int)(packed>>>1):(long)unchecked(8*(int)(packed>>>1)+6):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);
    public static long ScalarLoopIndexReturn(long packed,int fallback) { if((packed&1L)==0)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);int count=fallback<=0?0:fallback>=3?3:fallback;int result=unchecked((1+count)*(int)(packed>>>1));return fallback<3?(long)result:(long)unchecked(result+result); }
    public static long ScalarLoopCallIndexReturn(long packed,int fallback) { if((packed&1L)==0)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);int count=fallback<=0?0:fallback>=3?3:fallback;int result=unchecked((1+count)*(int)(packed>>>1)+count);return fallback<3?(long)result:(long)unchecked(result+result); }
    private static long ConsumeLoopPresentBreak(int? value,int fallback) { if(value.HasValue){int payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)break;payload=unchecked(payload+value.Value);}return unchecked(payload+payload);}return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback); }
    private static long ConsumeLoopCallBreak(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);int payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)break;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(unchecked(payload+value.Value));}return unchecked(payload+payload); }
    private static long ConsumeLoopPresentCallBreak(int? value,int fallback) { if(value.HasValue){int payload=value.Value;for(int i=0;i<3;i++){if(fallback>0)break;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(unchecked(payload+value.Value));}return unchecked(payload+payload);}return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback); }
    private static long ConsumeLoopIndexBreak(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);int payload=value.Value;for(int i=0;i<3;i++){if(i>=fallback)break;payload=unchecked(payload+value.Value);}return unchecked(payload+payload); }
    private static long ConsumeLoopCallIndexBreak(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);int payload=value.Value;for(int i=0;i<3;i++){if(i>=fallback)break;payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(unchecked(payload+value.Value));}return unchecked(payload+payload); }
    public static long ReadLoopBreak(int input,int fallback) { int? value=input<0?null:input;return ConsumeLoopBreak(value,fallback); }
    public static long ReadLoopPresentBreak(int input,int fallback) { int? value=input<0?null:input;return ConsumeLoopPresentBreak(value,fallback); }
    public static long ReadLoopCallBreak(int input,int fallback) { int? value=input<0?null:input;return ConsumeLoopCallBreak(value,fallback); }
    public static long ReadLoopPresentCallBreak(int input,int fallback) { int? value=input<0?null:input;return ConsumeLoopPresentCallBreak(value,fallback); }
    public static long ReadLoopIndexBreak(int input,int fallback) { int? value=input<0?null:input;return ConsumeLoopIndexBreak(value,fallback); }
    public static long ReadLoopCallIndexBreak(int input,int fallback) { int? value=input<0?null:input;return ConsumeLoopCallIndexBreak(value,fallback); }
    public static long ScalarLoopBreak(long packed,int fallback)=>(packed&1L)!=0?(long)unchecked((fallback>0?2:8)*(int)(packed>>>1)):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);
    public static long ScalarLoopCallBreak(long packed,int fallback)=>(packed&1L)!=0?(long)unchecked(fallback>0?2*(int)(packed>>>1):8*(int)(packed>>>1)+6):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);
    public static long ScalarLoopIndexBreak(long packed,int fallback)=>(packed&1L)!=0?(long)unchecked((2+2*(fallback<=0?0:fallback>=3?3:fallback))*(int)(packed>>>1)):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);
    public static long ScalarLoopCallIndexBreak(long packed,int fallback)=>(packed&1L)!=0?(long)unchecked((2+2*(fallback<=0?0:fallback>=3?3:fallback))*(int)(packed>>>1)+2*(fallback<=0?0:fallback>=3?3:fallback)):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);
    private static long ConsumeLoopPresentIf(int? value,int fallback) { if(value.HasValue){int payload=value.Value;for(int i=0;i<3;i++)if(fallback>0)payload=unchecked(payload+value.Value);return unchecked(payload+payload);}return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback); }
    private static long ConsumeLoopCallIf(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);int payload=value.Value;for(int i=0;i<3;i++)if(fallback>0)payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(unchecked(payload+value.Value));return unchecked(payload+payload); }
    private static long ConsumeLoopPresentCallIf(int? value,int fallback) { if(value.HasValue){int payload=value.Value;for(int i=0;i<3;i++)if(fallback>0)payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(unchecked(payload+value.Value));return unchecked(payload+payload);}return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback); }
    private static long ConsumeLoopPhi(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);int payload=value.Value;for(int i=0;i<3;i++)payload=fallback>0?unchecked(payload+value.Value):unchecked(payload^fallback);return unchecked(payload+payload); }
    private static long ConsumeLoopCallPhi(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);int payload=value.Value;for(int i=0;i<3;i++)payload=fallback>0?ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(unchecked(payload+value.Value)):unchecked(payload^fallback);return unchecked(payload+payload); }
    public static long ReadLoopIf(int input,int fallback) { int? value=input<0?null:input;return ConsumeLoopIf(value,fallback); }
    public static long ReadLoopPresentIf(int input,int fallback) { int? value=input<0?null:input;return ConsumeLoopPresentIf(value,fallback); }
    public static long ReadLoopCallIf(int input,int fallback) { int? value=input<0?null:input;return ConsumeLoopCallIf(value,fallback); }
    public static long ReadLoopPresentCallIf(int input,int fallback) { int? value=input<0?null:input;return ConsumeLoopPresentCallIf(value,fallback); }
    public static long ReadLoopPhi(int input,int fallback) { int? value=input<0?null:input;return ConsumeLoopPhi(value,fallback); }
    public static long ReadLoopCallPhi(int input,int fallback) { int? value=input<0?null:input;return ConsumeLoopCallPhi(value,fallback); }
    public static long ScalarLoopIf(long packed,int fallback)=>(packed&1L)!=0?(long)unchecked((fallback>0?8:2)*(int)(packed>>>1)):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);
    public static long ScalarLoopCallIf(long packed,int fallback)=>(packed&1L)!=0?(long)unchecked(fallback>0?8*(int)(packed>>>1)+6:2*(int)(packed>>>1)):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);
    public static long ScalarLoopPhi(long packed,int fallback)=>(packed&1L)!=0?(long)unchecked(fallback>0?8*(int)(packed>>>1):2*((int)(packed>>>1)^fallback)):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);
    public static long ScalarLoopCallPhi(long packed,int fallback)=>(packed&1L)!=0?(long)unchecked(fallback>0?8*(int)(packed>>>1)+6:2*((int)(packed>>>1)^fallback)):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);
    private static long ConsumeCountedPresentGetter3(int? value,int fallback) { if(value.HasValue){int payload=value.Value;for(int i=0;i<3;i++)payload=unchecked(payload+value.Value);return unchecked(payload+payload);}return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback); }
    private static long ConsumeCountedGetter0(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);int payload=value.Value;for(int i=0;i<0;i++)payload=unchecked(payload+value.Value);return unchecked(payload+payload); }
    private static long ConsumeCountedGetter5(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);int payload=value.Value;for(int i=0;i<5;i++)payload=unchecked(payload+value.Value);return unchecked(payload+payload); }
    private static long ConsumeCountedGetterCall(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);int payload=value.Value;for(int i=0;i<3;i++)payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(unchecked(payload+value.Value));return unchecked(payload+payload); }
    private static long ConsumeCountedPresentGetterCall(int? value,int fallback) { if(value.HasValue){int payload=value.Value;for(int i=0;i<3;i++)payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(unchecked(payload+value.Value));return unchecked(payload+payload);}return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback); }
    public static long ReadCountedGetter3(int input,int fallback) { int? value=input<0?null:input;return ConsumeCountedGetter3(value,fallback); }
    public static long ReadCountedPresentGetter3(int input,int fallback) { int? value=input<0?null:input;return ConsumeCountedPresentGetter3(value,fallback); }
    public static long ReadCountedGetter0(int input,int fallback) { int? value=input<0?null:input;return ConsumeCountedGetter0(value,fallback); }
    public static long ReadCountedGetter5(int input,int fallback) { int? value=input<0?null:input;return ConsumeCountedGetter5(value,fallback); }
    public static long ReadCountedGetterCall(int input,int fallback) { int? value=input<0?null:input;return ConsumeCountedGetterCall(value,fallback); }
    public static long ReadCountedPresentGetterCall(int input,int fallback) { int? value=input<0?null:input;return ConsumeCountedPresentGetterCall(value,fallback); }
    public static long ScalarCountedGetter3(long packed,int fallback)=>(packed&1L)!=0?(long)unchecked(8*(int)(packed>>>1)):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);
    public static long ScalarCountedGetter0(long packed,int fallback)=>(packed&1L)!=0?(long)unchecked(2*(int)(packed>>>1)):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);
    public static long ScalarCountedGetter5(long packed,int fallback)=>(packed&1L)!=0?(long)unchecked(12*(int)(packed>>>1)):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);
    public static long ScalarCountedGetterCall(long packed,int fallback)=>(packed&1L)!=0?(long)unchecked(8*(int)(packed>>>1)+6):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);
    private static long ConsumeCountedCall(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);int payload=value.Value;for(int i=0;i<3;i++)payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(payload);return unchecked(payload+payload); }
    private static long ConsumeCountedPresentCall(int? value,int fallback) { if(value.HasValue){int payload=value.Value;for(int i=0;i<3;i++)payload=ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(payload);return unchecked(payload+payload);}return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback); }
    public static long ReadCountedCall(int input,int fallback) { int? value=input<0?null:input;return ConsumeCountedCall(value,fallback); }
    public static long ReadCountedPresentCall(int input,int fallback) { int? value=input<0?null:input;return ConsumeCountedPresentCall(value,fallback); }
    public static long ScalarCountedCall(long packed,int fallback)=>(packed&1L)!=0?(long)unchecked(2*unchecked((int)(packed>>>1)+3)):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);
    private static long ConsumeCountedPresent3(int? value,int fallback) { if(value.HasValue){int payload=value.Value;for(int i=0;i<3;i++)payload=unchecked(payload+fallback);return unchecked(payload+payload);}return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback); }
    private static long ConsumeCounted5(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);int payload=value.Value;for(int i=0;i<5;i++)payload=unchecked(payload+fallback);return unchecked(payload+payload); }
    private static long ConsumeCounted0(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);int payload=value.Value;for(int i=0;i<0;i++)payload=unchecked(payload+fallback);return unchecked(payload+payload); }
    public static long ReadCounted3(int input,int fallback) { int? value=input<0?null:input;return ConsumeCounted3(value,fallback); }
    public static long ReadCountedPresent3(int input,int fallback) { int? value=input<0?null:input;return ConsumeCountedPresent3(value,fallback); }
    public static long ReadCounted5(int input,int fallback) { int? value=input<0?null:input;return ConsumeCounted5(value,fallback); }
    public static long ReadCounted0(int input,int fallback) { int? value=input<0?null:input;return ConsumeCounted0(value,fallback); }
    public static long ScalarCounted3(long packed,int fallback)=>(packed&1L)!=0?(long)unchecked(2*unchecked((int)(packed>>>1)+3*fallback)):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);
    public static long ScalarCounted5(long packed,int fallback)=>(packed&1L)!=0?(long)unchecked(2*unchecked((int)(packed>>>1)+5*fallback)):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);
    public static long ScalarCounted0(long packed,int fallback)=>(packed&1L)!=0?(long)unchecked(2*(int)(packed>>>1)):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);
    private static long ConsumeLocalPresent(int? value,int fallback) { if(value.HasValue){int payload=value.Value;if(fallback>0)payload=unchecked(payload+fallback);return unchecked(payload+payload);}return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback); }
    private static long ConsumeBothLocals(int? value,int fallback) { if(!value.HasValue){int empty=fallback;if(empty>0)empty=unchecked(empty+1);return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(empty);}int payload=value.Value;if(fallback>0)payload=unchecked(payload+fallback);return unchecked(payload+payload); }
    public static long ReadLocalTrailing(int input,int fallback) { int? value=input<0?null:input;return ConsumeLocalTrailing(value,fallback); }
    public static long ReadLocalPresent(int input,int fallback) { int? value=input<0?null:input;return ConsumeLocalPresent(value,fallback); }
    public static long ReadBothLocals(int input,int fallback) { int? value=input<0?null:input;return ConsumeBothLocals(value,fallback); }
    public static long ScalarLocalPayload(long packed,int fallback)=>(packed&1L)!=0?(long)unchecked(2*unchecked((int)(packed>>>1)+(fallback>0?fallback:0))):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);
    public static long ScalarBothLocals(long packed,int fallback)=>(packed&1L)!=0?(long)unchecked(2*unchecked((int)(packed>>>1)+(fallback>0?fallback:0))):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(unchecked(fallback+(fallback>0?1:0)));
    private static long ConsumeRepeatPresent(int? value,int fallback) { if(value.HasValue)return unchecked(value.Value+value.Value);return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback); }
    private static long ConsumeRepeatMerged(int? value,int fallback)=>(long)(value.HasValue?unchecked(value.Value+value.Value):ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback));
    public static long ReadRepeatTrailing(int input,int fallback) { int? value=input<0?null:input;return ConsumeRepeatTrailing(value,fallback); }
    public static long ReadRepeatPresent(int input,int fallback) { int? value=input<0?null:input;return ConsumeRepeatPresent(value,fallback); }
    public static long ReadRepeatMerged(int input,int fallback) { int? value=input<0?null:input;return ConsumeRepeatMerged(value,fallback); }
    public static long ScalarRepeatPayload(long packed,int fallback)=>(packed&1L)!=0?(long)unchecked((int)(packed>>>1)+(int)(packed>>>1)):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);
    private static long ConsumeMergedMask(int? value,int fallback)=>(long)(value.HasValue?((value.Value^fallback)&255):ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback));
    public static long ReadMergedAdd(int input,int fallback) { int? value=input<0?null:input;return ConsumeMergedAdd(value,fallback); }
    public static long ReadMergedMask(int input,int fallback) { int? value=input<0?null:input;return ConsumeMergedMask(value,fallback); }
    private static long ConsumeTrailingMask(int? value,int fallback) { if(!value.HasValue)return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);return (value.Value^fallback)&255; }
    public static long ReadTrailingAdd(int input,int fallback) { int? value=input<0?null:input;return ConsumeTrailingAdd(value,fallback); }
    public static long ReadTrailingMask(int input,int fallback) { int? value=input<0?null:input;return ConsumeTrailingMask(value,fallback); }
    private static long ConsumePayloadMask(int? value,int fallback) { if(value.HasValue)return (value.Value^fallback)&255;return ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback); }
    public static long ReadPayloadAdd(int input,int fallback) { int? value=input<0?null:input;return ConsumePayloadAdd(value,fallback); }
    public static long ReadPayloadMask(int input,int fallback) { int? value=input<0?null:input;return ConsumePayloadMask(value,fallback); }
    public static long ScalarPayloadAdd(long packed,int fallback)=>(packed&1L)!=0?(long)unchecked((int)(packed>>>1)+fallback):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);
    public static long ScalarPayloadMask(long packed,int fallback)=>(packed&1L)!=0?(long)(((int)(packed>>>1)^fallback)&255):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);
    private static long ConsumePresentConditional(int? value,int fallback) { if(value.HasValue)return value.Value;return fallback==0?-7:ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback); }
    public static long ReadPresentFirst(int input,int fallback) { int? value=input<0?null:input;return ConsumePresentFirst(value,fallback); }
    public static long ReadPresentConditional(int input,int fallback) { int? value=input<0?null:input;return ConsumePresentConditional(value,fallback); }
    private static long ConsumeEarlyConditional(int? value,int fallback) { if(!value.HasValue)return fallback==0?-7:ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);return value.Value; }
    public static long ReadEarlyReturn(int input,int fallback) { int? value=input<0?null:input;return ConsumeEarlyReturn(value,fallback); }
    public static long ReadEarlyConditional(int input,int fallback) { int? value=input<0?null:input;return ConsumeEarlyConditional(value,fallback); }
    private static long ConsumeGuardedConditional(int? value,int fallback)=>(long)(value.HasValue?value.Value:(fallback==0?-7:ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback)));
    public static long ReadGuardedValue(int input,int fallback) { int? value=input<0?null:input;return ConsumeGuardedValue(value,fallback); }
    public static long ReadGuardedConditional(int input,int fallback) { int? value=input<0?null:input;return ConsumeGuardedConditional(value,fallback); }
    private static long ConsumeExplicitConditional(int? value,int fallback)=>(long)(value.HasValue?value.GetValueOrDefault():(fallback==0?-7:ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback)));
    public static long ReadExplicitPresence(int input,int fallback) { int? value=input<0?null:input;return ConsumeExplicitPresence(value,fallback); }
    public static long ReadExplicitConditional(int input,int fallback) { int? value=input<0?null:input;return ConsumeExplicitConditional(value,fallback); }
    private static long ConsumeLazyDynamic(int? value,int fallback)=>(long)(value ?? ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback));
    private static long ConsumeLazyAdd(int? value,int fallback)=>(long)(value ?? unchecked(fallback+1));
    private static long ConsumeLazyMask(int? value,int fallback)=>(long)(value ?? ((fallback ^ -7)&255));
    private static long ConsumeLazyConditional(int? value,int fallback)=>(long)(value ?? (fallback==0?-7:unchecked(fallback+1)));
    private static long ConsumeLazyConditionalHelper(int? value,int fallback)=>(long)(value ?? (fallback==0?-7:ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback)));
    private static long ConsumeLazyLogical(int? value,int fallback)=>(long)(value ?? (fallback>0&&fallback<10?fallback:-7));
    private static long ConsumeLazyTwoHelpers(int? value,int fallback)=>(long)(value ?? (fallback>=0?ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback):ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(0)));
    public static long ReadLazyConditional(int input,int fallback) { int? value=input<0?null:input; return ConsumeLazyConditional(value,fallback); }
    public static long ReadLazyConditionalHelper(int input,int fallback) { int? value=input<0?null:input; return ConsumeLazyConditionalHelper(value,fallback); }
    public static long ReadLazyLogical(int input,int fallback) { int? value=input<0?null:input; return ConsumeLazyLogical(value,fallback); }
    public static long ReadLazyTwoHelpers(int input,int fallback) { int? value=input<0?null:input; return ConsumeLazyTwoHelpers(value,fallback); }
    public static long ScalarLazyConditional(long packed,int fallback)=>(packed&1L)!=0?(long)(int)(packed>>>1):(long)(fallback==0?-7:unchecked(fallback+1));
    public static long ScalarLazyConditionalHelper(long packed,int fallback)=>(packed&1L)!=0?(long)(int)(packed>>>1):(long)(fallback==0?-7:ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback));
    public static long ScalarLazyLogical(long packed,int fallback)=>(packed&1L)!=0?(long)(int)(packed>>>1):(long)(fallback>0&&fallback<10?fallback:-7);
    public static long ScalarLazyTwoHelpers(long packed,int fallback)=>(packed&1L)!=0?(long)(int)(packed>>>1):(long)(fallback>=0?ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback):ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(0));
    public static long ReadLazyDynamic(int input,int fallback) { int? value=input<0?null:input; return ConsumeLazyDynamic(value,fallback); }
    public static long ReadLazyAdd(int input,int fallback) { int? value=input<0?null:input; return ConsumeLazyAdd(value,fallback); }
    public static long ReadLazyMask(int input,int fallback) { int? value=input<0?null:input; return ConsumeLazyMask(value,fallback); }
    public static long ScalarLazyDynamic(long packed,int fallback)=>(packed&1L)!=0?(long)(int)(packed>>>1):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(fallback);
    public static long ScalarLazyAdd(long packed,int fallback)=>(packed&1L)!=0?(long)(int)(packed>>>1):(long)unchecked(fallback+1);
    public static long ScalarLazyMask(long packed,int fallback)=>(packed&1L)!=0?(long)(int)(packed>>>1):(long)((fallback ^ -7)&255);
    public static long ReadLazyHelper(int input) { int? value=input<0?null:input; return ConsumeLazyHelper(value); }
    public static long ReadLazyHelperScalar(long packed)=>(packed&1L)!=0?(long)(int)(packed>>>1):(long)ManagedPortSmoke.Dependency.MetadataOnlyCallee.AdjustFallback(-7);
    public static long ReadCombinedHelper(int input) { int? value=input<0?null:input; return ConsumeCombinedHelper(value); }
    public static long ReadCombinedHelperScalar(long packed) { int next=ManagedPortSmoke.Dependency.FallbackOwner<int>.MethodIdentity<int>(-7); return (packed&1L)!=0?(long)(int)(packed>>>1):(long)next; }
    public static long ReadOwnerHelper(int input) { int? value=input<0?null:input; return ConsumeOwnerHelper(value); }
    public static long ReadOwnerHelperScalar(long packed) { int next=ManagedPortSmoke.Dependency.FallbackOwner<int>.Identity(-7); return (packed&1L)!=0?(long)(int)(packed>>>1):(long)next; }
    public static long ReadGenericHelper(int input) { int? value=input<0?null:input; return ConsumeGenericHelper(value); }
    public static long ReadGenericHelperScalar(long packed) { int next=ManagedPortSmoke.Dependency.MetadataOnlyCallee.FallbackIdentity<int>(-7); return (packed&1L)!=0?(long)(int)(packed>>>1):(long)next; }
    public static long ReadBinaryHelper(int input) { int? value=input<0?null:input; return ConsumeBinaryHelper(value); }
    public static long ReadBinaryHelperScalar(long packed) { int next=ManagedPortSmoke.Dependency.MetadataOnlyCallee.CombineFallback(-7,1); return (packed&1L)!=0?(long)(int)(packed>>>1):(long)next; }
    public static long ReadConstantHelper(int input) { int? value=input<0?null:input;return ConsumeConstantHelper(value); }
    public static long ReadConstantHelperScalar(long packed) { int next=AdjustHelperScalar(-7); return (packed&1L)!=0?(long)(int)(packed>>>1):(long)next; }
    public static long ReadExternalHelper(int input,int fallback) { int? value=input<0?null:input;return ConsumeExternalHelper(value,fallback); }
    private static long ConsumeHelper(int? value,int fallback)=>(long)value.GetValueOrDefault(AdjustHelper(fallback));
    public static long ReadHelper(int input,int fallback) { int? value=input<0?null:input;return ConsumeHelper(value,fallback); }
    private static int AdjustHelperScalar(int fallback)=>unchecked(fallback+1);
    public static long ReadHelperScalar(long packed,int fallback) { int next=AdjustHelperScalar(fallback); return (packed&1L)!=0?(long)(int)(packed>>>1):(long)next; }
}
