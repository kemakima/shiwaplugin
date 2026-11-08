using System; using System.IO; using System.Collections.Generic; using UnityEngine; using ClothWrinkle;
static class T {
  static void Main(string[] a) {
    int NA=128, NY=192; float R=0.08f, L=0.6f;
    var rest=new List<Vector3>(); var uv=new List<Vector2>(); var tris=new List<int>();
    // two UV islands: front half (angles 0..pi) and back half, with a gap, to test skirts
    for(int j=0;j<=NY;j++) for(int i=0;i<=NA;i++){ float ang=2*Mathf.PI*i/NA; float y=L*j/NY;
      rest.Add(new Vector3(R*Mathf.Cos(ang), y, R*Mathf.Sin(ang))); uv.Add(new Vector2((float)i/NA*0.9f+0.05f,(float)j/NY*0.9f+0.05f)); }
    for(int j=0;j<NY;j++) for(int i=0;i<NA;i++){ int v=j*(NA+1)+i; tris.AddRange(new[]{v,v+NA+1,v+1, v+1,v+NA+1,v+NA+2}); }
    float bend = float.Parse(a[0])*Mathf.Deg2Rad; float cb=(float)Math.Cos(bend), sb=(float)Math.Sin(bend); var piv=new Vector3(0,L/2,0);
    var cur=new List<Vector3>();
    foreach(var p in rest){ float w=Mathf.Clamp01((p.y-(L/2-0.05f))/0.1f); var d=p-piv; var p2=new Vector3(d.x, d.y*cb-d.z*sb, d.y*sb+d.z*cb)+piv; cur.Add(Vector3.Lerp(p,p2,w)); }
    var part=new MeshPart{ Rest=rest.ToArray(), Uv=uv.ToArray(), Tris=tris.ToArray() }; part.Axis=new Vector3[rest.Count]; part.AxisCoord=new float[rest.Count];
    for(int k=0;k<rest.Count;k++){ part.Axis[k]=Vector3.up; part.AxisCoord[k]=rest[k].y; }
    part.Weld=(int[])typeof(WrinkleBaker).GetMethod("BuildWeld",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static).Invoke(null,new object[]{part.Rest,null});
    int wc=0; foreach(var x in part.Weld) wc=Math.Max(wc,x+1); part.WeldCount=wc;
    var parts=new List<MeshPart>{part};
    var s=new WrinkleSettings{Enabled=true};
    if (a.Length>1) s.DeepFolds=float.Parse(a[1]);
    part.Colors=new Color32[rest.Count];
    PrepTri(part);
    int res=512;
    var b=WrinkleBaker.BuildBinding(parts,res,1.5f); var M=Realtime.PatternTensor(parts);
    var sw=System.Diagnostics.Stopwatch.StartNew();
    var job=new PatternJob{Binding=b,Tensor=M,Settings=s,Key="k",NormalOrig=null,NW=res,NH=res,NormalBase=true,Toon=true,DetailOrig=null,DW=res,DH=res,DetailBase=true};
    job.Run(); Console.WriteLine("job "+job.Ms+"ms err="+job.Error);
    Realtime.GlobalWeld(parts, 0.0015f); part.CurBuf=cur;
    sw.Restart(); for(int i=0;i<30;i++) Realtime.UpdateAmounts(parts,s,false); Console.WriteLine($"amounts x30 {sw.ElapsedMilliseconds}ms tris {tris.Count/3}");
    // UV mesh (arrays only; Mesh creation fails outside Unity)
    try { Realtime.BuildUvMesh(part,res,res,1.5f); } catch(Exception e){ Console.WriteLine("BuildUvMesh mesh step: "+e.GetType().Name); }
    Console.WriteLine($"uv verts {part.UvVerts?.Length} tris {part.UvTris?.Length/3} (body {tris.Count/3})");
    Realtime.FillUvColors(part, s.DeepFolds>0.001f, false);
    Console.WriteLine($"maxA1 {part.MaxA1} maxA2 {part.MaxA2}");
    // CPU composite: rasterize UV mesh (incl. skirts) with alpha into a1/a2 maps
    var a1=new float[res*res]; var a2=new float[res*res];
    Raster(part, part.UvCol1, a1, res); Raster(part, part.UvCol2, a2, res);
    var img=new byte[res*res*3]; var imgN=new byte[res*res*3];
    var light=new Vector3(0.3f,0.8f,0.5f).normalized;
    for(int y=0;y<res;y++) for(int x=0;x<res;x++){
      int i=y*res+x; float w1=a1[i], w2=a2[i];
      Color32 nb=job.NormalBasePx[i], n1=job.NormalFull1[i], n2=job.NormalFull2!=null?job.NormalFull2[i]:n1;
      Color32 db=job.DetailBasePx[i], d1=job.DetailFull1[i], d2=job.DetailFull2!=null?job.DetailFull2[i]:d1;
      Vector3 nn=Lerp3(Lerp3(N(nb),N(n1),w1),N(n2),w2); 
      float G=Mathf.Lerp(Mathf.Lerp(db.g,d1.g,w1),d2.g,w2)/255f, Bv=Mathf.Lerp(Mathf.Lerp(db.b,d1.b,w1),d2.b,w2)/255f;
      // surface normal in "tangent" frame: z out, y up (v). light from above-front
      float lam=Vector3.Dot(nn.normalized,light);
      float lit = lam>0.35f?1f:0f; // toon ramp
      lit = Mathf.Min(lit, 1f-G*0.8f); // drawn shadow
      var col=Color.Lerp(new Color(0.62f,0.45f,0.55f), new Color(0.95f,0.7f,0.8f), lit);
      float lw=0.35f; float lineMul=Mathf.Pow(lw,Bv);
      col*=lineMul;
      img[i*3]=(byte)(col.r*255); img[i*3+1]=(byte)(col.g*255); img[i*3+2]=(byte)(col.b*255);
      // smooth lit (normal only)
      float sh=Mathf.Clamp01(0.25f+0.75f*lam);
      imgN[i*3]=imgN[i*3+1]=imgN[i*3+2]=(byte)(sh*255);
    }
    File.WriteAllBytes("toon_"+a[0]+".raw",img); File.WriteAllBytes("lit_"+a[0]+".raw",imgN);
    var am=new byte[res*res]; for(int i=0;i<am.Length;i++) am[i]=(byte)(Mathf.Clamp01(a1[i])*255); File.WriteAllBytes("a1_"+a[0]+".raw",am);
  }
  static Vector3 N(Color32 c)=> new Vector3(c.r/127.5f-1f,c.g/127.5f-1f,c.b/127.5f-1f);
  static Vector3 Lerp3(Vector3 x, Vector3 y, float t)=> x+(y-x)*t;
  static void Raster(MeshPart p, Color32[] col, float[] dst, int res){
    var v=p.UvVerts; var t=p.UvTris;
    for(int k=0;k+2<t.Length;k+=3){
      Vector2 A=new Vector2(v[t[k]].x*res,v[t[k]].y*res), B=new Vector2(v[t[k+1]].x*res,v[t[k+1]].y*res), C=new Vector2(v[t[k+2]].x*res,v[t[k+2]].y*res);
      float ar=(B.x-A.x)*(C.y-A.y)-(C.x-A.x)*(B.y-A.y); if(Mathf.Abs(ar)<1e-9f) continue;
      int x0=Math.Max(0,(int)Math.Floor(Math.Min(A.x,Math.Min(B.x,C.x)))), x1=Math.Min(res-1,(int)Math.Ceiling(Math.Max(A.x,Math.Max(B.x,C.x))));
      int y0=Math.Max(0,(int)Math.Floor(Math.Min(A.y,Math.Min(B.y,C.y)))), y1=Math.Min(res-1,(int)Math.Ceiling(Math.Max(A.y,Math.Max(B.y,C.y))));
      for(int y=y0;y<=y1;y++) for(int x=x0;x<=x1;x++){ float px=x+0.5f, py=y+0.5f;
        float b0=((B.x-px)*(C.y-py)-(C.x-px)*(B.y-py))/ar, b1=((C.x-px)*(A.y-py)-(A.x-px)*(C.y-py))/ar, b2=1-b0-b1;
        if(b0<0||b1<0||b2<0) continue; float al=(col[t[k]].a*b0+col[t[k+1]].a*b1+col[t[k+2]].a*b2)/255f;
        int i=y*res+x; dst[i]=al+dst[i]*(1-al); } } }
  static void PrepTri(MeshPart p){
    int nt=p.Tris.Length/3; p.TriL1=new float[nt]; p.TriBx=new float[nt]; p.TriBy=new float[nt]; p.TriArea=new float[nt]; p.TriAx=new float[nt]; p.TriAy=new float[nt];
    for(int t=0;t<nt;t++){ int ia=p.Tris[t*3], ib=p.Tris[t*3+1], ic=p.Tris[t*3+2]; var e1=p.Rest[ib]-p.Rest[ia]; var e2=p.Rest[ic]-p.Rest[ia];
      var n=Vector3.Cross(e1,e2); float ar=n.magnitude,l1=e1.magnitude; var T=e1/l1; var B=Vector3.Cross(n/ar,T);
      p.TriL1[t]=l1; p.TriBx[t]=Vector3.Dot(e2,T); p.TriBy[t]=Vector3.Dot(e2,B); p.TriArea[t]=ar; float x=Vector3.Dot(Vector3.up,T), y=Vector3.Dot(Vector3.up,B), len=Mathf.Sqrt(x*x+y*y); p.TriAx[t]=x/len; p.TriAy[t]=y/len; }
    double sum=0; for(int t=0;t<nt;t++) sum+=p.TriArea[t]; p.RefAreaSum=sum; }
}
