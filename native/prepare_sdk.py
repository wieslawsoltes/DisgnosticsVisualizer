"""Extend the pinned official sample IL rewriter in the build directory, never the SDK checkout."""
from pathlib import Path
import hashlib,sys,shutil
source=Path(sys.argv[1])/'src/tests/profiler/native/rejitprofiler'
out=Path(sys.argv[2]);out.mkdir(parents=True,exist_ok=True)
# SHA-256 of the exact files in the pinned official runtime revision.
expected={'ilrewriter.h': '8b5e9697c1093c0f990ad080643893cd928585ceaab85b684578b8a452cee0db', 'ilrewriter.cpp': '1e416e29ed7247f57b2efa26a26cff18ac890bdc2f3c2029da1f86bdfbfe753c', 'sigparse.h': '7e9459bef2f6874c67c4e39cd5d640cc3c05614b25519a1d23309276c5c548c3', 'sigparse.cpp': '1e0fc177f8cdaf8db77da3f28244041b458aaa5f6c089b0505a8d16eeb6edd54'}
for name,sha in expected.items():
    data=(source/name).read_bytes().replace(b"\r\n", b"\n") # Git may expand canonical LF on Windows.
    if hashlib.sha256(data).hexdigest()!=sha:raise SystemExit('Unsupported SDK rewriter revision: '+name)
    (out/name).write_bytes(data)
p=out/'ilrewriter.h';text=p.read_text()
needle='    HRESULT Export();'
assert text.count(needle)==1
text=text.replace(needle,'''    // DiagnosticsVisualizer extension: explicit outer-finally and local-signature editing.
    // Original exception clauses stay in their original order; the outer clause is last.
    mdToken LocalSignature() const { return m_tkLocalVarSig; }
    unsigned ExceptionCount() const { return m_nEH; }
    unsigned EstimatedMaxStack() const { return m_maxStack; }
    void LocalSignature(mdToken token) { m_tkLocalVarSig=token; m_fGenerateTinyHeader=false; }
    void SealOriginalEnd(ILInstr* end) {
        for(unsigned i=0;i<m_nEH;++i) if(m_pEH[i].m_pTryEnd==&m_IL)m_pEH[i].m_pTryEnd=end;
    }
    void AddFinally(ILInstr* first,ILInstr* tryEnd,ILInstr* handler,ILInstr* handlerEnd) {
        auto* clauses=new EHClause[m_nEH+1];
        for(unsigned i=0;i<m_nEH;++i)clauses[i]=m_pEH[i];
        clauses[m_nEH++]={COR_ILEXCEPTION_CLAUSE_FINALLY,first,tryEnd,handler,handlerEnd,{0}};
        delete[] m_pEH;m_pEH=clauses;m_fGenerateTinyHeader=false;
    }
    HRESULT Export();''')
p.write_text(text)
