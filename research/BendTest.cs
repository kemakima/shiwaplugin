using System; using System.IO; using System.Collections.Generic; using UnityEngine; using ClothWrinkle;
static class T {
  static void Main(string[] a) {
    int NA=64, NY=96; float R=0.08f, L=0.6f;
    var rest=new List<Vector3>(); var uv=new List<Vector2>(); var tris=new List<int>();
    for(int j=0;j<=NY;j++) for(int i=0;i<=NA;i++){ float ang=2*Mathf.PI*i/NA; float y=L*j/NY;
      rest.Add(new Vector3(R*Mathf.Cos(ang), y, R*Mathf.Sin(ang))); uv.Add(new Vector2((float)i/NA*0.9f+0.05f,(float)j/NY*0.9f+0.05f)); }
    for(int j=0;j<NY;j++) for(int i=0;i<NA;i++){ int v=j*(NA+1)+i; tris.AddRange(new[]{v,v+NA+1,v+1, v+1,v+NA+1,v+NA+2}); }
    // bend: bone2 rotates about x axis at y=L/2 by angle, LBS blend over +-0.05
    float bend = float.Parse(a[0])*Mathf.Deg2Rad;
    float cb=(float)Math.Cos(bend), sb=(float)Math.Sin(bend); var piv=new Vector3(0,L/2,0);
    var cur=new Vector3[rest.Count];
    for(int k=0;k<rest.Count;k++){ var p=rest[k]; float w=Mathf.Clamp01((p.y-(L/2-0.05f))/0.1f);
      var d=p-piv; var p2=new Vector3(d.x, d.y*cb-d.z*sb, d.y*sb+d.z*cb)+piv; cur[k]=Vector3.Lerp(p,p2,w); }
    var part=new MeshPart{ Rest=rest.ToArray(), Uv=uv.ToArray(), Tris=tris.ToArray() }; part.Axis=new Vector3[rest.Count]; for(int k=0;k<rest.Count;k++) part.Axis[k]=Vector3.up;
    part.Weld = (int[])typeof(WrinkleBaker).GetMethod("BuildWeld",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)
       .Invoke(null,new object[]{part.Rest,null}); 
    int wc=0; foreach(var x in part.Weld) wc=Math.Max(wc,x+1); part.WeldCount=wc;
    var parts=new List<MeshPart>{part};
    var s=new WrinkleSettings{Enabled=true};
    var sw=System.Diagnostics.Stopwatch.StartNew();
    var b=WrinkleBaker.BuildBinding(parts,512,1.5f);
    WrinkleBaker.StrainStats st; var M=WrinkleBaker.ComputeTensor(parts,new[]{cur},s,out st);
    var h=WrinkleBaker.BuildHeight(b,M,s);
    var px=WrinkleBaker.Compose(h,512,null,1024,1024,s.Strength);
    Console.WriteLine($"comp {st.MaxCompression:F3} str {st.MaxStretch:F3} amt {st.MaxAmount:F3} {sw.ElapsedMilliseconds}ms");
    using(var f=File.Create("n.raw")) foreach(var c in px){f.WriteByte(c.r);f.WriteByte(c.g);f.WriteByte(c.b);}
    using(var f=File.Create("h.raw")) foreach(var v in h){ f.WriteByte((byte)Mathf.Clamp(255+v*255,0,255)); }
  }
}
