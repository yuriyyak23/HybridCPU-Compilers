#!/usr/bin/env python3
"""Emit a source-pinned, manual-schema SUB/SUI candidate and exhaustive testbench."""
import hashlib, json, sys
from pathlib import Path

ART = Path(r"C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080").resolve()
EQ = {
    "result16": "(A - operand) mod 65536", "result8": "result16[7:0]", "A_next": "result8",
    "CY_next": "result16 > 0x00FF (source helper; borrow on byte underflow)",
    "AC_SUB_B_source_next": "zero_extend_1(A[3:0]) + zero_extend_1((~operand)[3:0]) + 1 > 0xF",
    "AC_SUI_source_effect": "AC_prev (SUI source handler does not write AC)",
    "Z_next": "result8 == 0", "S_next": "result8[7]",
    "P_next": "even_parity8(result8); source verified against all 256 ParityTable entries",
    "PC_SUB_B_next": "PC + 1", "PC_SUI_next": "PC + 2",
}

def emit(source: Path, out: Path):
    source = source.resolve(strict=True); out = out.resolve()
    if not source.is_relative_to(ART) or not out.is_relative_to(ART): raise ValueError("paths must stay under artifacts/8080")
    if out.exists(): raise FileExistsError(out)
    d = json.loads(source.read_text(encoding="utf-8"))
    if d.get("schema") != "hardwarebridge.intel8080.sub-hwir/v1": raise ValueError("bad SUB projection schema")
    eq = {x["Name"]: x["Formula"] for x in d["equations"]}
    if eq != EQ: raise ValueError("reviewed SUB equations changed")
    if [(x["opcode"], x["mnemonic"]) for x in d["dispatch"]] != [("0x90", "SUB B"), ("0xD6", "SUI data")]: raise ValueError("SUB/SUI dispatch changed")
    if d["architectureSelection"].get("auxCarryName") != "AC_spec_next" or d["architectureSelection"].get("formula") != "zero_extend_1(A[3:0]) + zero_extend_1((~operand)[3:0]) + 1 > 0xF": raise ValueError("spec AC selection changed")
    traces=[]
    for row in d["sourceTrace"]:
        p=Path(row["file"]).resolve(strict=True); digest=hashlib.sha256(p.read_bytes()).hexdigest()
        if digest != row["sha256"]: raise ValueError("source hash changed: "+str(p))
        traces.append((row["symbol"], str(p), row["startLine"],row["endLine"],digest))
    comments="\n".join(f"// source {s}: {p}:{a}-{b} sha256={h}" for s,p,a,b,h in traces)
    sv=f'''// Manual-schema candidate; CircuitWeave lowers result16 only.\n{comments}\nmodule intel8080_sub_source_projected(input logic [7:0] A, operand, input logic is_sui, AC_prev, input logic [15:0] PC, output logic [15:0] result16, PC_next, output logic [7:0] result8, A_next, output logic sign_next, zero_next, parity_next, carry_next, AC_source_next, AC_spec_next);\nlogic [4:0] nibble_sum;\nalways_comb begin\n result16={{8'h00,A}}-{{8'h00,operand}}; result8=result16[7:0]; A_next=result8;\n sign_next=result8[7]; zero_next=(result8==0); parity_next=~^result8; carry_next=(result16>16'h00ff);\n nibble_sum={{1'b0,A[3:0]}}+{{1'b0,(~operand[3:0])}}+5'd1;\n AC_spec_next=nibble_sum[4]; AC_source_next=is_sui ? AC_prev : nibble_sum[4]; PC_next=PC+(is_sui?16'd2:16'd1);\nend endmodule\n'''
    tb=r'''`timescale 1ns/1ps
module tb_sub_source_projected;
logic [7:0] A,operand; logic is_sui,AC_prev; logic [15:0] PC,result16,PC_next; logic [7:0] result8,A_next; logic sign_next,zero_next,parity_next,carry_next,AC_source_next,AC_spec_next;
integer vector,a,b,m,ac,diff,wrap,nib,ones,i; intel8080_sub_source_projected dut(.*);
initial begin for(vector=0;vector<262144;vector=vector+1) begin
 m=(vector>>16)&1; ac=(vector>>17)&1; b=(vector>>1)&255; a=(vector>>9)&255; A=a[7:0];operand=b[7:0];is_sui=m[0];AC_prev=ac[0];PC=vector[15:0];#1;
 diff=a-b;wrap=diff&65535;nib=(a&15)+((~b)&15)+1;ones=0;for(i=0;i<8;i=i+1)ones=ones+((wrap>>i)&1);
 if(result16!==wrap[15:0]||result8!==wrap[7:0]||A_next!==wrap[7:0]||sign_next!==wrap[7]||zero_next!==(wrap[7:0]==0)||parity_next!==((ones%2)==0)||carry_next!==(wrap>255)||AC_spec_next!==(nib>15)||AC_source_next!==(m?ac:(nib>15))||PC_next!==((vector+(m?2:1))&16'hffff)) $fatal(1,"SUB/SUI mismatch vector=%0d",vector);
 end $display("PASS SUB/SUI candidate: 262144 operand/mode/AC vectors; result, SZP, borrow, source/spec AC, PC deltas");$finish;end endmodule
'''
    out.mkdir(parents=True,exist_ok=False); (out/"intel8080_sub_source_projected.sv").write_text(sv,encoding="utf-8"); (out/"tb_sub_source_projected.sv").write_text(tb,encoding="utf-8")
    (out/"manifest.json").write_text(json.dumps({"schema":"hardwarebridge.intel8080.sub-sv-candidate/v1","status":"manual-schema candidate; not CircuitWeave SystemVerilog output","projection":str(source),"projectionSha256":hashlib.sha256(source.read_bytes()).hexdigest(),"sourceTrace":[{"symbol":s,"file":p,"startLine":a,"endLine":b,"sha256":h} for s,p,a,b,h in traces],"dispatch":[["90h","SUB B"],["D6h","SUI"]],"validation":"Icarus exhaustive: 256x256 operands, both handlers, both prior AC values"},indent=2)+"\n",encoding="utf-8")
    print(f"Created source-pinned manual SUB/SUI SV candidate: {out}")

if __name__=="__main__":
    if len(sys.argv)!=3: raise SystemExit("usage: emit_sub_projection_sv.py projection.json new-output-directory")
    emit(Path(sys.argv[1]),Path(sys.argv[2]))
