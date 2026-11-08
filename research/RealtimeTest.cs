using System; using System.IO; using System.Collections.Generic; using UnityEngine; using ClothWrinkle;
static class T {
  static void Main(string[] a) {
    int NA=128, NY=192; float R=0.08f, L=0.6f;
    var rest=new List<Vector3>(); var uv=new List<Vector2>(); var tris=new List<int>();
    for(int j=0;j<=NY;j++) for(int i=0;i<=NA;i++){ float ang=2*Mathf.PI*i/NA; float y=L*j/NY;
      rest.Add(new Vector3(R*Mathf.Cos(ang), y, R*Mathf.Sin(ang))); uv.Add(new Vector2((float)i/NA*0.9f+0.05f,(float)j/NY*0.9f+0.05f)); }
    for(int j=0;j<NY;j++) for(int i=0;i<NA;i++){ int v=j*(NA+1)+i; tris.AddRange(new[]{v,v+NA+1,v+1, v+1,v+NA+1,v+NA+2}); }
    float bend = float.Parse(a[0])*Mathf.Deg2Rad; float cb=(float)Math.Cos(bend), sb=(float)Math.Sin(bend); var piv=new Vector3(0,L/2,0);
    var cur=new List<Vector3>();
    foreach(var p in rest){ float w=Mathf.Clamp01((p.y-(L/2-0.05f))/0.1f); var d=p-piv; var p2=new Vector3(d.x, d.y*cb-d.z*sb, d.y*sb+d.z*cb)+piv; cur.Add(Vector3.Lerp(p,p2,w)); }
    var part=new MeshPart{ Rest=rest.ToArray(), Uv=uv.ToArray(), Tris=tris.ToArray() }; part.Axis=new Vector3[rest.Count]; for(int k=0;k<rest.Count;k++) part.Axis[k]=Vector3.up;
    part.Weld=(int[])typeof(WrinkleBaker).GetMethod("BuildWeld",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static).Invoke(null,new object[]{part.Rest,null});
    int wc=0; foreach(var x in part.Weld) wc=Math.Max(wc,x+1); part.WeldCount=wc;
    var parts=new List<MeshPart>{part}; var s=new WrinkleSettings{Enabled=true};
    // PreparePart without Mesh creation: replicate triangle frame part via reflection-free path
    part.Colors=new Color32[rest.Count];
    var sw=System.Diagnostics.Stopwatch.StartNew();
    PrepTri(part);
    Console.WriteLine("prep "+sw.ElapsedMilliseconds+"ms");
    var b=WrinkleBaker.BuildBinding(parts,512,1.5f); var M=Realtime.PatternTensor(parts);
    sw.Restart(); var h=WrinkleBaker.BuildHeight(b,M,s,true); Console.WriteLine("pattern "+sw.ElapsedMilliseconds+"ms");
    part.CurBuf=cur;
    sw.Restart(); for(int i=0;i<30;i++) Realtime.UpdateAmounts(parts,s,false); Console.WriteLine($"amounts x30 {sw.ElapsedMilliseconds}ms tris {tris.Count/3}");
    // composite on CPU: height * per-texel alpha (approx via vertex nearest) -> write alpha image along u at fixed v
    var img=new byte[(NA+1)*(NY+1)]; for(int k=0;k<img.Length;k++) img[k]=part.Colors[k].a;
    File.WriteAllBytes("alpha.raw",img);
    var hb=new byte[512*512]; for(int k=0;k<hb.Length;k++) hb[k]=(byte)Mathf.Clamp(255+h[k]*255,0,255); File.WriteAllBytes("pat.raw",hb);
  }
  static void PrepTri(MeshPart p){
    int nt=p.Tris.Length/3; p.TriL1=new float[nt]; p.TriBx=new float[nt]; p.TriBy=new float[nt]; p.TriArea=new float[nt];
    for(int t=0;t<nt;t++){ int ia=p.Tris[t*3], ib=p.Tris[t*3+1], ic=p.Tris[t*3+2]; var e1=p.Rest[ib]-p.Rest[ia]; var e2=p.Rest[ic]-p.Rest[ia];
      var n=Vector3.Cross(e1,e2); float ar=n.magnitude,l1=e1.magnitude; var T=e1/l1; var B=Vector3.Cross(n/ar,T);
      p.TriL1[t]=l1; p.TriBx[t]=Vector3.Dot(e2,T); p.TriBy[t]=Vector3.Dot(e2,B); p.TriArea[t]=ar; }
    double sum=0; for(int t=0;t<nt;t++) sum+=p.TriArea[t]; p.RefAreaSum=sum; }
}
