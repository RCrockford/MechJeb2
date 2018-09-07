using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace MuMech {
    public class PontryaginLaunch : PontryaginBase {
        public PontryaginLaunch(double mu, Vector3d r0, Vector3d v0, Vector3d pv0, Vector3d pr0, double dV) : base(mu, r0, v0, pv0, pr0, dV)
        {
        }

        double rTm;
        double vTm;
        double gamma;
        double inc;
        double smaT;
        Vector3d hT;

        // 5-constraint PEG with fixed LAN
        public void flightangle5constraint(double rTm, double vTm, double gamma, Vector3d hT)
        {
            this.rTm = rTm / r_scale;
            this.vTm = vTm / v_scale;
            this.gamma = gamma;
            this.hT = -hT.xzy / r_scale / v_scale;  // KSP's Orbit class has h swizzled, but not 'inverse rotated', and pointed the wrong direction
            bcfun = flightangle5constraint;
        }

        private void flightangle5constraint(double[] yT, double[] z)
        {
            Vector3d rf = new Vector3d(yT[0], yT[1], yT[2]);
            Vector3d vf = new Vector3d(yT[3], yT[4], yT[5]);
            Vector3d pvf = new Vector3d(yT[6], yT[7], yT[8]);
            Vector3d prf = new Vector3d(yT[9], yT[10], yT[11]);

            Vector3d hf = Vector3d.Cross(rf, vf);
            Vector3d hmiss = hf.normalized - hT.normalized;

            /* 5 constraints */
            z[0] = ( rf.magnitude * rf.magnitude - rTm * rTm ) / 2.0;
            z[1] = ( vf.magnitude * vf.magnitude - vTm * vTm ) / 2.0;
            z[2] = Vector3d.Dot(rf, vf) - rf.magnitude * vf.magnitude * Math.Sin(gamma);
            z[3] = hmiss[0];
            z[4] = hmiss[2];

            /* transversality - free argp */
            z[5] = Vector3d.Dot(Vector3d.Cross(prf, rf) + Vector3d.Cross(pvf, vf), hT);
        }

        // 4-constraint PEG with free LAN
        public void flightangle4constraint(double rTm, double vTm, double gamma, double inc)
        {
            this.rTm = rTm / r_scale;
            this.vTm = vTm / v_scale;
            this.gamma = gamma;
            this.inc = inc;
            bcfun = flightangle4constraint;
        }

        private void flightangle4constraint(double[] yT, double[] z)
        {
            Vector3d rf = new Vector3d(yT[0], yT[1], yT[2]);
            Vector3d vf = new Vector3d(yT[3], yT[4], yT[5]);
            Vector3d pvf = new Vector3d(yT[6], yT[7], yT[8]);
            Vector3d prf = new Vector3d(yT[9], yT[10], yT[11]);

            Vector3d n = new Vector3d(0, -1, 0);  /* angular momentum vectors point south in KSP and we're in xzy coords */
            Vector3d rn = Vector3d.Cross(rf, n);
            Vector3d vn = Vector3d.Cross(vf, n);
            Vector3d hf = Vector3d.Cross(rf, vf);

            z[0] = ( rf.magnitude * rf.magnitude - rTm * rTm ) / 2.0;
            z[1] = ( vf.magnitude * vf.magnitude - vTm * vTm ) / 2.0;
            z[2] = Vector3d.Dot(n, hf) - hf.magnitude * Math.Cos(inc);
            z[3] = Vector3d.Dot(rf, vf) - rf.magnitude * vf.magnitude * Math.Sin(gamma);
            z[4] = rTm * rTm * ( Vector3d.Dot(vf, prf) - vTm * Math.Sin(gamma) / rTm * Vector3d.Dot(rf, prf) ) -
                vTm * vTm * ( Vector3d.Dot(rf, pvf) - rTm * Math.Sin(gamma) / vTm * Vector3d.Dot(vf, pvf) );
            z[5] = Vector3d.Dot(hf, prf) * Vector3d.Dot(hf, rn) + Vector3d.Dot(hf, pvf) * Vector3d.Dot(hf, vn);
        }

        public override void Bootstrap(double t0)
        {
            // build arcs off of ksp stages, with coasts
            List<Arc> arcs = new List<Arc>();
            for(int i = 0; i < stages.Count; i++)
            {
                //if (i != 0)
                    //arcs.Add(new Arc(new Stage(this, m0: stages[i].m0, isp: 0, thrust: 0)));
                arcs.Add(new Arc(stages[i]));
            }

            arcs[arcs.Count-1].infinite = true;

            // allocate y0
            y0 = new double[arcIndex(arcs, arcs.Count)];

            // update initial position and guess for first arc
            double ve = g0 * stages[0].isp;
            tgo = ve * stages[0].m0 / stages[0].thrust * ( 1 - Math.Exp(-dV/ve) );
            tgo_bar = tgo / t_scale;

            // initialize coasts to zero
            /*
            for(int i = 0; i < arcs.Count; i++)
            {
                if (arcs[i].thrust == 0)
                {
                    int index = arcIndex(arcs, i, parameters: true);
                    y0[index] = 0;
                    y0[index+1] = 0;
                }
            }
            */

            // initialize overall burn time
            y0[0] = tgo_bar;
            y0[1] = 0;

            UpdateY0(arcs);

            // seed continuity initial conditions
            yf = new double[arcs.Count*13];
            multipleIntegrate(y0, yf, arcs, initialize: true);

            /*
            for(int j = 0; j < y0.Length; j++)
                Debug.Log("bootstrap - y0[" + j + "] = " + y0[j]);
                */

            Debug.Log("running optimizer");

            if ( !runOptimizer(arcs) )
            {
                for(int k = 0; k < y0.Length; k++)
                    Debug.Log("failed - y0[" + k + "] = " + y0[k]);
                Debug.Log("optimizer failed");
                y0 = null;
                return;
            }

            if (y0[0] < 0)
            {
                for(int k = 0; k < y0.Length; k++)
                    Debug.Log("failed - y0[" + k + "] = " + y0[k]);
                Debug.Log("optimizer failed2");
                y0 = null;
                return;
            }

            Debug.Log("optimizer done");

            Solution new_sol = new Solution(t_scale, v_scale, r_scale, t0);
            multipleIntegrate(y0, new_sol, arcs, 10);

            Debug.Log("arcs.Count = " + arcs.Count);
            Debug.Log("segments.Count = " + new_sol.segments.Count);

            //for(int i = arcs.Count-1; i > 0 ; i--)
                //InsertCoast(arcs, i);
            InsertCoast(arcs, arcs.Count-1, new_sol);

            Debug.Log("running optimizer2");

            if ( !runOptimizer(arcs) )
            {
                for(int k = 0; k < y0.Length; k++)
                    Debug.Log("failed - y0[" + k + "] = " + y0[k]);
                Debug.Log("optimizer failed");
                y0 = null;
                return;
            }

            if (y0[0] < 0)
            {
                for(int k = 0; k < y0.Length; k++)
                    Debug.Log("failed - y0[" + k + "] = " + y0[k]);
                Debug.Log("optimizer failed2");
                y0 = null;
                return;
            }

            Debug.Log("optimizer done");

            new_sol = new Solution(t_scale, v_scale, r_scale, t0);
            multipleIntegrate(y0, new_sol, arcs, 10);

            arcs[arcs.Count-1].infinite = false;

            Debug.Log("running optimizer3");

            if ( !runOptimizer(arcs) )
            {
                for(int k = 0; k < y0.Length; k++)
                    Debug.Log("failed - y0[" + k + "] = " + y0[k]);
                Debug.Log("optimizer failed");
                y0 = null;
                return;
            }

            if (y0[0] < 0)
            {
                for(int k = 0; k < y0.Length; k++)
                    Debug.Log("failed - y0[" + k + "] = " + y0[k]);
                Debug.Log("optimizer failed2");
                y0 = null;
                return;
            }

            Debug.Log("optimizer done");

            new_sol = new Solution(t_scale, v_scale, r_scale, t0);
            multipleIntegrate(y0, new_sol, arcs, 10);

            for(int k = 0; k < y0.Length; k++)
                Debug.Log("new y0[" + k + "] = " + y0[k]);

            this.solution = new_sol;
            Debug.Log("done with bootstrap");

            yf = new double[arcs.Count*13];
            multipleIntegrate(y0, yf, arcs);

            for(int k = 0; k < yf.Length; k++)
                Debug.Log("new yf[" + k + "] = " + yf[k]);

            Debug.Log("optimizer hT = " + hT.magnitude * r_scale * v_scale);
            Debug.Log("r_scale = " + r_scale);
            Debug.Log("v_scale = " + v_scale);
        }
    }
}
