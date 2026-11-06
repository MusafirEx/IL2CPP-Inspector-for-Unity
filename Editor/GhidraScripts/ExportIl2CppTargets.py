#@category IL2CPPInspector
# Ghidra Jython post-script. Arguments: script.json output_directory comma-separated-class-names
# This script does not reproduce original managed method bodies.
import json
import os
import codecs
from ghidra.app.decompiler import DecompInterface

args = getScriptArgs()
if len(args) < 3:
    raise Exception("Expected: <script.json> <outputdir> <keywords>")
mapping_path, output_dir, selection = args[:3]
keywords = [value.strip().lower() for value in selection.split(",") if value.strip()]
if not keywords:
    raise Exception("Set at least one class or method keyword")
if not os.path.isdir(output_dir):
    os.makedirs(output_dir)
with open(mapping_path, "rb") as stream:
    data = json.loads(stream.read().decode("utf-8"))

methods = data.get("ScriptMethod", [])
matched = [item for item in methods if any(
    word in item.get("Name", "").lower() for word in keywords
)]
matched = matched[:80]
memory = currentProgram.getMemory()
base = currentProgram.getImageBase()
decompiler = DecompInterface()
decompiler.openProgram(currentProgram)
out = codecs.open(os.path.join(output_dir, "targets_pseudocode.txt"), "w", "utf-8")

def candidate_address(value):
    # Il2CppDumper usually stores native addresses. Check both absolute and base-relative.
    number = int(str(value), 0) if isinstance(value, basestring) else long(value)
    for candidate in [toAddr(number), base.add(number)]:
        if memory.contains(candidate):
            return candidate
    return None

try:
    out.write(u"IL2CPP Inspector / Ghidra pseudocode (approximation)\n")
    out.write(u"Selected {} of {} mapped methods\n".format(len(matched), len(methods)))
    for item in matched:
        if monitor.isCancelled():
            break
        name = unicode(item.get("Name", "unnamed"))
        out.write(u"\n// {}\n".format(name))
        try:
            address = candidate_address(item.get("Address", 0))
            if address is None:
                out.write(u"// Address outside the loaded binary. Check RVA and image base.\n")
                continue
            function = getFunctionAt(address)
            if function is None:
                try:
                    createFunction(address, None)
                    function = getFunctionAt(address)
                except Exception:
                    pass
            if function is None:
                out.write(u"// No function at {}\n".format(unicode(address)))
                continue
            result = decompiler.decompileFunction(function, 60, monitor)
            if result.decompileCompleted() and result.getDecompiledFunction() is not None:
                out.write(unicode(result.getDecompiledFunction().getC()))
                out.write(u"\n")
            else:
                out.write(u"// Decompiler could not produce code for {}\n".format(unicode(address)))
        except Exception as exception:
            out.write(u"// Analysis error: {}\n".format(unicode(exception)))
finally:
    out.close()
    decompiler.dispose()
print("IL2CPP Inspector: exported {} methods".format(len(matched)))
