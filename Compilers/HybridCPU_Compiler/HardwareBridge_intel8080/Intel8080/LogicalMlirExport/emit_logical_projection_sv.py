#!/usr/bin/env python3
"""Emit a source-pinned logical source/spec candidate and exhaustive Icarus bench."""
import hashlib, json, sys
from pathlib import Path

ART = Path(r"C:\Users\Yuriy\Desktop\CircuitWeave-main\artifacts\8080").resolve()
SCHEMA = "hardwarebridge.intel8080.logical-hwir/v1"
DISPATCH = [("0xA0","ANA B"),("0xA7","ANA A"),("0xAA","XRA D"),("0xAF","XRA A"),("0xB0","ORA B"),("0xB6","ORA M"),("0xE6","ANI data"),("0xEE","XRI data"),("0xF6","ORI data")]
EXPECTED = {
 "ANA_result":"A & operand","XRA_result":"A ^ operand","ORA_B_result":"A | B","ORA_M_result":"A | M",
 "AOR_result":"A & operand (source helper is named AOR but performs AND)",
 "ANA_CY_next":"0","ANA_AC_next":"0 (source helper)",
 "XRA_CY_next":"result8[0] (source CalcCarryFlag(byte) behavior)","XRA_AC_effect":"AC_prev (XOR does not write AC)",
 "ORA_B_CY_next":"0","ORA_B_AC_next":"0",
 "ORA_M_CY_next":"result8[0] (source CalcCarryFlag(byte) behavior)","ORA_M_AC_effect":"AC_prev (ORA_M does not write AC)",
 "ORI_source_result":"A & data (AOR source helper defect)","ORI_spec_result":"A | data (Intel 8080 ORI)",
 "XRI_source_result":"A ^ data (XRI calls XOR helper)","XRI_CY_source_next":"result8[0] (source CalcCarryFlag(byte) behavior)",
 "XRI_AC_source_effect":"AC_prev (XRI calls XOR; XOR does not write AC)","PC_XRI_source_next":"PC + 2 (XOR helper increments once; XRI wrapper increments once)",
 "SZP":"Z=(result8==0), S=result8[7], P=even_parity8(result8)"
}

def emit(projection: Path, out: Path):
    projection=projection.resolve(strict=True); out=out.resolve()
    if not projection.is_relative_to(ART) or not out.is_relative_to(ART): raise ValueError("paths must remain under artifacts/8080")
    if out.exists(): raise FileExistsError(out)
    data=json.loads(projection.read_text(encoding="utf-8"))
    if data.get("schema")!=SCHEMA: raise ValueError("unsupported logical projection schema")
    eq={row["Name"]:row["Formula"] for row in data.get("equations",[])}
    if eq!=EXPECTED: raise ValueError("reviewed logical equation table changed")
    if [(row["opcode"],row["mnemonic"]) for row in data.get("dispatch",[])]!=DISPATCH: raise ValueError("source logical dispatch changed")
    if data.get("architectureSelection",{}).get("ORI_operation")!="A | data": raise ValueError("architectural ORI decision changed")
    traces=[]
    for row in data.get("sourceTrace",[]):
        path=Path(row["file"]).resolve(strict=True); digest=hashlib.sha256(path.read_bytes()).hexdigest()
        if digest!=row["sha256"]: raise ValueError("source hash changed: "+str(path))
        traces.append({"symbol":row["symbol"],"file":str(path),"startLine":row["startLine"],"endLine":row["endLine"],"sha256":digest})
    if len(traces)!=13: raise ValueError("incomplete logical source trace")
    comments="\n".join(f"// source {row['symbol']}: {row['file']}:{row['startLine']}-{row['endLine']} sha256={row['sha256']}" for row in traces)
    sv=f'''// Manual-schema candidate; CircuitWeave output is result-only and stored separately.
{comments}
// Modes: 0 ANA/ANI, 1 XRA, 2 ORA B, 3 ORA M, 4 ORI source helper, 5 ORI spec RTL.
module intel8080_logical_source_projected(input logic [7:0] A, operand, input logic [2:0] mode, input logic AC_prev,
 output logic [7:0] source_result, spec_result, output logic source_cy, source_ac, spec_cy, spec_ac,
 output logic source_s,source_z,source_p,spec_s,spec_z,spec_p);
 always_comb begin
  case(mode)
   3'd0: begin source_result=A&operand; spec_result=A&operand; end
   3'd1: begin source_result=A^operand; spec_result=A^operand; end
   3'd2,3'd3: begin source_result=A|operand; spec_result=A|operand; end
   3'd4: begin source_result=A&operand; spec_result=A|operand; end
   default: begin source_result=A|operand; spec_result=A|operand; end
  endcase
  source_s=source_result[7]; source_z=(source_result==0); source_p=~^source_result;
  spec_s=spec_result[7]; spec_z=(spec_result==0); spec_p=~^spec_result;
  source_cy=((mode==3'd1)||(mode==3'd3)) ? source_result[0] : 1'b0;
  source_ac=(mode==3'd1||mode==3'd3) ? AC_prev : 1'b0;
  spec_cy=1'b0; spec_ac=(mode==3'd0) ? (A[3]|operand[3]) : 1'b0;
 end
endmodule
'''
    tb=r'''`timescale 1ns/1ps
module tb_logical_source_projected;
logic [7:0] A,operand,source_result,spec_result; logic [2:0] mode; logic AC_prev;
logic source_cy,source_ac,spec_cy,spec_ac,source_s,source_z,source_p,spec_s,spec_z,spec_p;
integer vector,a,b,m,ac,sr,hr,i,ones_s,ones_h;
intel8080_logical_source_projected dut(.*);
initial begin
 for(vector=0;vector<786432;vector=vector+1) begin
  m=vector/131072;ac=(vector>>16)&1;b=(vector>>8)&255;a=vector&255;
  mode=m[2:0];AC_prev=ac[0];A=a[7:0];operand=b[7:0];#1;
  case(m) 0,4:sr=a&b;1:sr=a^b;2,3,5:sr=a|b;default:sr=0;endcase
  case(m) 0:hr=a&b;1:hr=a^b;default:hr=a|b;endcase
  ones_s=0;ones_h=0;for(i=0;i<8;i=i+1)begin ones_s=ones_s+((sr>>i)&1);ones_h=ones_h+((hr>>i)&1);end
  if(source_result!==sr[7:0]||spec_result!==hr[7:0]||source_s!==sr[7]||source_z!==(sr==0)||source_p!==((ones_s%2)==0)||
     spec_s!==hr[7]||spec_z!==(hr==0)||spec_p!==((ones_h%2)==0)||
     source_cy!==(((m==1)||(m==3))?(sr&1):0)||source_ac!==(((m==1)||(m==3))?ac:0)||spec_cy!==1'b0||spec_ac!==(m==0?(((a>>3)&1)|((b>>3)&1)):0))
   $fatal(1,"logical source/spec mismatch vector=%0d mode=%0d A=%02h operand=%02h",vector,m,A,operand);
 end
 $display("PASS logical projection: 786432 exhaustive A/operand/mode/AC vectors; source vs spec result/flags");$finish;
end endmodule
'''
    out.mkdir(parents=True,exist_ok=False)
    (out/"intel8080_logical_source_projected.sv").write_text(sv,encoding="utf-8")
    (out/"tb_logical_source_projected.sv").write_text(tb,encoding="utf-8")
    manifest={"schema":"hardwarebridge.intel8080.logical-sv-candidate/v1","status":"manual-schema candidate; not CircuitWeave-generated SV","projection":str(projection),"projectionSha256":hashlib.sha256(projection.read_bytes()).hexdigest(),"sourceTrace":traces,"dispatch":DISPATCH,"validation":"Icarus exhaustive 6 x 256 x 256 x 2; compares source/spec result, SZP, CY and AC","limits":["no opcode decode","no PC or instruction timing","no memory or CPU state"]}
    (out/"manifest.json").write_text(json.dumps(manifest,indent=2)+"\n",encoding="utf-8")
    print("Created manual logical candidate and exhaustive bench: "+str(out))

if __name__=="__main__":
    if len(sys.argv)!=3: raise SystemExit("usage: emit_logical_projection_sv.py projection.json new-output-directory")
    emit(Path(sys.argv[1]),Path(sys.argv[2]))
