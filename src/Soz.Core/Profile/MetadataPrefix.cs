using System.Text.Json;

namespace Soz.Core.Profile;

/// <summary>Bounded streaming JSON metadata projection. Skipped strings are never allocated, even on multi-MB lines.</summary>
internal sealed class MetadataPrefix(Stream stream, int budget)
{
    private int _peek = -2;
    public int Consumed { get; private set; }
    public bool End => Peek() < 0;
    private int Peek()
    {
        if (_peek == -2) _peek = Consumed >= budget ? -1 : stream.ReadByte();
        return _peek;
    }
    private int Take() { var n = Peek(); _peek = -2; if(n>=0) Consumed++; return n; }
    private void White() { while(Peek() is 9 or 10 or 13 or 32) Take(); }
    private void Expect(int n) { White(); if(Take()!=n) throw new JsonException(); }
    public void NextLine() { while(Take() is var c && c >= 0 && c != 10) { } }
    public Dictionary<string,string>? Read(bool codex)
    {
        if(Consumed==0 && Peek()==0xEF)
            if(Take()!=0xEF || Take()!=0xBB || Take()!=0xBF) throw new JsonException();
        White();
        if(End) return null;
        var fields=Object(0,codex);
        return fields;
    }
    private Dictionary<string,string> Object(int depth,bool codex)
    {
        if(depth>32) throw new JsonException();
        Expect('{');
        var fields=new Dictionary<string,string>(StringComparer.Ordinal);
        Dictionary<string,string>? payload=null;
        string? type=null;
        White();
        if(Peek()=='}'){Take();return fields;}
        while(!End)
        {
            var key=String(64);
            Expect(':');White();
            if(codex && key=="payload" && Peek()=='{') payload=Object(depth+1,false);
            else if(key is "cwd" or "sessionId" or "id" or "gitBranch" or "timestamp" or "parent_session_id" or "parentSessionId" or "agentId" or "type" or "title" or "customTitle" && Peek()=='"')
            {
                var value=String(4096);
                if(key=="type")
                {
                    type=value;
                    // Kayıt türü yalnız en üst düzey kayıtta ve Claude biçiminde tutulur ("_type"); biçim denetimi için.
                    if(depth==0&&!codex&&value is not null)fields["_type"]=value;
                }
                else if(value is not null) fields[key]=value;
            }
            else
            {
                // "cwd" alanı var ama değeri metin değil (örn. null): alan tanınıyor, yalnız boş. Biçim denetimi için işaretlenir.
                if(key=="cwd")fields["_cwdKey"]="1";
                Skip(depth+1);
            }
            White();var separator=Take();
            if(separator=='}') break;
            if(separator!=',') throw new JsonException();
            White();
        }
        if(codex)
        {
            if(type is not ("session_meta" or "turn_context")) return new();
            if(payload is not null)foreach(var kv in payload)fields[kv.Key]=kv.Value;
        }
        return fields;
    }
    private string? String(int max)
    {
        White();if(Take()!='"')throw new JsonException();
        var bytes=max>0?new byte[max+2]:null;
        var length=0;var overflow=false;var escaped=false;
        if(bytes is not null)bytes[length++]=(byte)'"';
        while(!End)
        {
            var c=Take();
            if(c<32)throw new JsonException();
            if(bytes is not null && length<bytes.Length)bytes[length++]=(byte)c;else overflow=true;
            if(c=='"'&&!escaped)
            {
                if(bytes is null||overflow)return null;
                try{return JsonSerializer.Deserialize<string>(bytes.AsSpan(0,length));}
                finally{Array.Clear(bytes);}
            }
            if(escaped)escaped=false;else if(c=='\\')escaped=true;
        }
        throw new JsonException();
    }
    private void Skip(int depth)
    {
        if(depth>32)throw new JsonException();
        White();var first=Peek();
        if(first=='"'){String(0);return;}
        if(first is '{' or '[')
        {
            Take();var closing=first=='{'?'}':']';
            while(!End)
            {
                White();if(Peek()==closing){Take();return;}
                if(Peek() is ',' or ':'){Take();continue;}
                Skip(depth+1);
            }
            throw new JsonException();
        }
        var count=0;
        while(Peek() is var c && c>=0 && c is not (',' or '}' or ']' or 9 or 10 or 13 or 32)){Take();count++;}
        if(count==0)throw new JsonException();
    }
}
