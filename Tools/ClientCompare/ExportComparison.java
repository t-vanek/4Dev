// Ghidra headless post-script. Export discovered functions and readable pseudocode.
// @category 4Dev
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import java.io.*;
import java.nio.charset.StandardCharsets;

public class ExportComparison extends GhidraScript {
    @Override public void run() throws Exception {
        String[] args = getScriptArgs();
        File out = new File(args[0]); out.mkdirs();
        DecompInterface di = new DecompInterface();
        di.openProgram(currentProgram);
        int discovered = currentProgram.getFunctionManager().getFunctionCount();
        int completed = 0, failed = 0, thunks = 0;
        try (PrintWriter c = new PrintWriter(new File(out, "decompiled.c"), StandardCharsets.UTF_8);
             PrintWriter csv = new PrintWriter(new File(out, "functions.tsv"), StandardCharsets.UTF_8);
             PrintWriter xrefs = new PrintWriter(new File(out, "strings-xrefs.tsv"), StandardCharsets.UTF_8)) {
            c.println("/* Ghidra pseudocode, inferred types. This is not original source and is not buildable. */");
            csv.println("address\tname\tbody_bytes\tstatus");
            for (Function f : currentProgram.getFunctionManager().getFunctions(true)) {
                if (monitor.isCancelled()) break;
                if (f.isThunk() || f.isExternal()) {
                    thunks++; csv.println(f.getEntryPoint()+"\t"+f.getName()+"\t"+f.getBody().getNumAddresses()+"\tthunk");
                    continue;
                }
                DecompileResults result = di.decompileFunction(f, 5, monitor);
                boolean ok = result.decompileCompleted() && result.getDecompiledFunction() != null;
                csv.println(f.getEntryPoint()+"\t"+f.getName()+"\t"+f.getBody().getNumAddresses()+"\t"+(ok?"ok":"failed"));
                if (ok) {
                    completed++;
                    c.println("\n/* Address: "+f.getEntryPoint()+"; "+f.getName()+" */");
                    c.println(result.getDecompiledFunction().getC());
                } else { failed++; c.println("/* Failed "+f.getEntryPoint()+": "+result.getErrorMessage().replace("*/", "")+" */"); }
                if ((completed + failed) % 1000 == 0) println("Exported "+completed+" functions, failed "+failed+" / discovered "+discovered);
            }
            xrefs.println("string_address\tfunction_address\tfunction_name\ttext");
            for (Data data : currentProgram.getListing().getDefinedData(true)) {
                if (!data.hasStringValue()) continue;
                String value = String.valueOf(data.getValue()).replace("\t", " ").replace("\r", " ").replace("\n", " ");
                for (Reference ref : getReferencesTo(data.getAddress())) {
                    Function f = getFunctionContaining(ref.getFromAddress());
                    if (f != null) xrefs.println(data.getAddress()+"\t"+f.getEntryPoint()+"\t"+f.getName()+"\t"+value);
                }
            }
        } finally { di.dispose(); }
        try (PrintWriter summary = new PrintWriter(new File(out, "summary.json"), StandardCharsets.UTF_8)) {
            summary.println("{\"program\":\""+currentProgram.getName()+"\",\"discovered\":"+discovered+",\"completed\":"+completed+",\"failed\":"+failed+",\"thunks\":"+thunks+"}");
        }
        println("EXPORT COMPLETE: "+completed+" decompiled; "+failed+" failures; "+thunks+" thunks.");
    }
}
