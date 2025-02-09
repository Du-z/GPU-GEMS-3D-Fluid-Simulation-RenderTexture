﻿using UnityEngine;
using UnityEngine.Rendering;

namespace FluidSim3DProject
{
	public class FireFluidSim : MonoBehaviour 
	{
		//DONT CHANGE THESE
		const int READ = 0;
		const int WRITE = 1;
		const int PHI_N_HAT = 0;
		const int PHI_N_1_HAT = 1;
		
		public enum ADVECTION { NORMAL = 1, BFECC = 2, MACCORMACK = 3 };
		
		//You can change this but you must change the same value in all the compute shader's to the same
		//Must be a pow2 number
		const int NUM_THREADS = 8;
		
		//You can change this or even use Time.DeltaTime but large time steps can cause numerical errors
		const float TIME_STEP = 0.1f;
		
		public ADVECTION m_denstyAdvectionType = ADVECTION.NORMAL;
		public ADVECTION m_reactionAdvectionType = ADVECTION.NORMAL;
		public int m_width = 128;
		public int m_height = 128;
		public int m_depth = 128;
		public int m_iterations = 10;
		public float m_vorticityStrength = 1.0f;
		public float m_densityAmount = 1.0f;
		public float m_densityDissipation = 0.999f;
		public float m_densityBuoyancy = 1.0f;
		public float m_densityWeight = 0.0125f;
		public float m_temperatureAmount = 10.0f;
		public float m_temperatureDissipation = 0.995f;
		public float m_reactionAmount = 1.0f;
		public float m_reactionDecay = 0.001f;
		public float m_reactionExtinguishment = 0.01f;
		public float m_velocityDissipation = 0.995f;
		public float m_inputRadius = 0.04f;
		public Vector4 m_inputPos = new Vector4(0.5f,0.1f,0.5f,0.0f);
		
		float m_ambientTemperature = 0.0f;
		
		public ComputeShader m_applyImpulse, m_applyAdvect, m_computeVorticity;
		public ComputeShader m_computeDivergence, m_computeJacobi, m_computeProjection;
		public ComputeShader m_computeConfinement, m_computeObstacles, m_applyBuoyancy;
		
		Vector4 m_size;
		public RenderTexture[] m_density, m_velocity, m_pressure, m_temperature, m_phi, m_reaction;
		public RenderTexture m_temp3f, m_obstacles;

		void Start () 
		{
			QualitySettings.vSyncCount = 1;
			Application.targetFrameRate = 60;

			//Dimension sizes must be pow2 numbers
			m_width = Mathf.ClosestPowerOfTwo(m_width);
			m_height = Mathf.ClosestPowerOfTwo(m_height);
			m_depth = Mathf.ClosestPowerOfTwo(m_depth);
			
			//Put all dimension sizes in a vector for easy parsing to shader and also prevents user changing
			//dimension sizes during play
			m_size = new Vector4(m_width, m_height, m_depth, 0.0f);
			
			//Create all the buffers needed
			
			m_density = new RenderTexture[2];
			m_density[READ] = CreateRenderTexture(RenderTextureFormat.RFloat, $"Density {nameof(READ)}");
			m_density[WRITE] = CreateRenderTexture(RenderTextureFormat.RFloat, $"Density {nameof(WRITE)}");
			
			m_temperature = new RenderTexture[2];
			m_temperature[READ] = CreateRenderTexture(RenderTextureFormat.RFloat, $"Temperature {nameof(READ)}");
			m_temperature[WRITE] = CreateRenderTexture(RenderTextureFormat.RFloat, $"Temperature {nameof(WRITE)}");
			
			m_reaction = new RenderTexture[2];
			m_reaction[READ] = CreateRenderTexture(RenderTextureFormat.RFloat, $"Reaction {nameof(READ)}");
			m_reaction[WRITE] = CreateRenderTexture(RenderTextureFormat.RFloat, $"Reaction {nameof(WRITE)}");
			
			m_phi = new RenderTexture[2];
			m_phi[PHI_N_HAT] = CreateRenderTexture(RenderTextureFormat.RFloat, $"Phi {nameof(PHI_N_HAT)}");
			m_phi[PHI_N_1_HAT] = CreateRenderTexture(RenderTextureFormat.RFloat, $"Phi {nameof(PHI_N_1_HAT)}");
			
			m_velocity = new RenderTexture[2];
			m_velocity[READ] = CreateRenderTexture(RenderTextureFormat.ARGBFloat, $"Pressure {nameof(READ)}");
			m_velocity[WRITE] = CreateRenderTexture(RenderTextureFormat.ARGBFloat, $"Pressure {nameof(WRITE)}");
			
			m_pressure = new RenderTexture[2];
			m_pressure[READ] = CreateRenderTexture(RenderTextureFormat.RFloat, $"Pressure {nameof(READ)}");
			m_pressure[WRITE] = CreateRenderTexture(RenderTextureFormat.RFloat, $"Pressure {nameof(WRITE)}");
			
			m_obstacles = CreateRenderTexture(RenderTextureFormat.RFloat, "Obstacles");
			
			m_temp3f = CreateRenderTexture(RenderTextureFormat.ARGBFloat, "temp3f");
			
			//Any areas that are obstacles need to be masked of in the obstacle buffer
			//At the moment is only the border around the edge of the buffers to enforce non-slip boundary conditions
			ComputeObstacles();
		
		}

		private RenderTexture CreateRenderTexture(RenderTextureFormat format, string name)
		{
			var rt = new RenderTexture(m_width, m_height, 0, format);
			rt.name = name;
			rt.dimension = TextureDimension.Tex3D;
			rt.volumeDepth = m_depth;
			rt.enableRandomWrite = true;
			rt.anisoLevel = 0;
			rt.filterMode = FilterMode.Bilinear;

			rt.Create();
			return rt;
		}
		
		void Swap(RenderTexture[] buffer)
		{
			RenderTexture tmp = buffer[READ];
			buffer[READ] = buffer[WRITE];
			buffer[WRITE] = tmp;
		}
		
		void ComputeObstacles()
		{
			m_computeObstacles.SetVector("_Size", m_size);
			m_computeObstacles.SetTexture(0, "_Write", m_obstacles);
			m_computeObstacles.Dispatch(0, (int)m_size.x/NUM_THREADS, (int)m_size.y/NUM_THREADS, (int)m_size.z/NUM_THREADS);
			
		}
		
		void ApplyImpulse(float dt, float amount, RenderTexture[] buffer)
		{
			m_applyImpulse.SetVector("_Size", m_size);
			m_applyImpulse.SetFloat("_Radius", m_inputRadius);
			m_applyImpulse.SetFloat("_Amount", amount);
			m_applyImpulse.SetFloat("_DeltaTime", dt);
			m_applyImpulse.SetVector("_Pos", m_inputPos);
			
			m_applyImpulse.SetTexture(0, "_Read", buffer[READ]);
			m_applyImpulse.SetTexture(0, "_Write", buffer[WRITE]);
			
			m_applyImpulse.Dispatch(0, (int)m_size.x/NUM_THREADS, (int)m_size.y/NUM_THREADS, (int)m_size.z/NUM_THREADS);
			
			Swap(buffer);
		}
		
		void ApplyExtinguishmentImpulse(float dt, float amount, RenderTexture[] buffer)
		{
			m_applyImpulse.SetVector("_Size", m_size);
			m_applyImpulse.SetFloat("_Radius", m_inputRadius);
			m_applyImpulse.SetFloat("_Amount", amount);
			m_applyImpulse.SetFloat("_DeltaTime", dt);
			m_applyImpulse.SetVector("_Pos", m_inputPos);
			m_applyImpulse.SetFloat("_Extinguishment", m_reactionExtinguishment);
			
			m_applyImpulse.SetTexture(1, "_Read", buffer[READ]);
			m_applyImpulse.SetTexture(1, "_Write", buffer[WRITE]);
			m_applyImpulse.SetTexture(1, "_Reaction", m_reaction[READ]);
			
			m_applyImpulse.Dispatch(1, (int)m_size.x/NUM_THREADS, (int)m_size.y/NUM_THREADS, (int)m_size.z/NUM_THREADS);
			
			Swap(buffer);
		}
		
		void ApplyBuoyancy(float dt)
		{
			m_applyBuoyancy.SetVector("_Size", m_size);
			m_applyBuoyancy.SetVector("_Up", new Vector4(0,1,0,0));
			m_applyBuoyancy.SetFloat("_Buoyancy", m_densityBuoyancy);
			m_applyBuoyancy.SetFloat("_AmbientTemperature", m_ambientTemperature);
			m_applyBuoyancy.SetFloat("_Weight", m_densityWeight);
			m_applyBuoyancy.SetFloat("_DeltaTime", dt);
			
			m_applyBuoyancy.SetTexture(0, "_Write", m_velocity[WRITE]);
			m_applyBuoyancy.SetTexture(0, "_Velocity", m_velocity[READ]);
			m_applyBuoyancy.SetTexture(0, "_Density", m_density[READ]);
			m_applyBuoyancy.SetTexture(0, "_Temperature", m_temperature[READ]);
			
			m_applyBuoyancy.Dispatch(0, (int)m_size.x/NUM_THREADS, (int)m_size.y/NUM_THREADS, (int)m_size.z/NUM_THREADS);
			
			Swap(m_velocity);
		}
		
		void ApplyAdvection(float dt, float dissipation, float decay, RenderTexture[] buffer, float forward = 1.0f)
		{
			m_applyAdvect.SetVector("_Size", m_size);
			m_applyAdvect.SetFloat("_DeltaTime", dt);
			m_applyAdvect.SetFloat("_Dissipate", dissipation);
			m_applyAdvect.SetFloat("_Forward", forward);
			m_applyAdvect.SetFloat("_Decay", decay);
			
			m_applyAdvect.SetTexture((int)ADVECTION.NORMAL, "_Read1f", buffer[READ]);
			m_applyAdvect.SetTexture((int)ADVECTION.NORMAL, "_Write1f", buffer[WRITE]);
			m_applyAdvect.SetTexture((int)ADVECTION.NORMAL, "_Velocity", m_velocity[READ]);
			m_applyAdvect.SetTexture((int)ADVECTION.NORMAL, "_Obstacles", m_obstacles);
			
			m_applyAdvect.Dispatch((int)ADVECTION.NORMAL, (int)m_size.x/NUM_THREADS, (int)m_size.y/NUM_THREADS, (int)m_size.z/NUM_THREADS);
			
			Swap(buffer);
		}
		
		void ApplyAdvection(float dt, float dissipation, float decay, RenderTexture read, RenderTexture write, float forward = 1.0f)
		{
			m_applyAdvect.SetVector("_Size", m_size);
			m_applyAdvect.SetFloat("_DeltaTime", dt);
			m_applyAdvect.SetFloat("_Dissipate", dissipation);
			m_applyAdvect.SetFloat("_Forward", forward);
			m_applyAdvect.SetFloat("_Decay", decay);
			
			m_applyAdvect.SetTexture((int)ADVECTION.NORMAL, "_Read1f", read);
			m_applyAdvect.SetTexture((int)ADVECTION.NORMAL, "_Write1f", write);
			m_applyAdvect.SetTexture((int)ADVECTION.NORMAL, "_Velocity", m_velocity[READ]);
			m_applyAdvect.SetTexture((int)ADVECTION.NORMAL, "_Obstacles", m_obstacles);
			
			m_applyAdvect.Dispatch((int)ADVECTION.NORMAL, (int)m_size.x/NUM_THREADS, (int)m_size.y/NUM_THREADS, (int)m_size.z/NUM_THREADS);
		}
		
		void ApplyAdvectionBFECC(float dt, float dissipation, float decay, RenderTexture[] buffer)
		{
			m_applyAdvect.SetVector("_Size", m_size);
			m_applyAdvect.SetFloat("_DeltaTime", dt);
			m_applyAdvect.SetFloat("_Dissipate", dissipation);
			m_applyAdvect.SetFloat("_Forward", 1.0f);
			m_applyAdvect.SetFloat("_Decay", decay);
			
			m_applyAdvect.SetTexture((int)ADVECTION.BFECC, "_Read1f", buffer[READ]);
			m_applyAdvect.SetTexture((int)ADVECTION.BFECC, "_Write1f", buffer[WRITE]);
			m_applyAdvect.SetTexture((int)ADVECTION.BFECC, "_Phi_n_hat", m_phi[PHI_N_HAT]);
			m_applyAdvect.SetTexture((int)ADVECTION.BFECC, "_Velocity", m_velocity[READ]);
			m_applyAdvect.SetTexture((int)ADVECTION.BFECC, "_Obstacles", m_obstacles);
			
			m_applyAdvect.Dispatch((int)ADVECTION.BFECC, (int)m_size.x/NUM_THREADS, (int)m_size.y/NUM_THREADS, (int)m_size.z/NUM_THREADS);
			
			Swap(buffer);
		}
		
		void ApplyAdvectionMacCormack(float dt, float dissipation, float decay, RenderTexture[] buffer)
		{
			m_applyAdvect.SetVector("_Size", m_size);
			m_applyAdvect.SetFloat("_DeltaTime", dt);
			m_applyAdvect.SetFloat("_Dissipate", dissipation);
			m_applyAdvect.SetFloat("_Forward", 1.0f);
			m_applyAdvect.SetFloat("_Decay", decay);
			
			m_applyAdvect.SetTexture((int)ADVECTION.MACCORMACK, "_Read1f", buffer[READ]);
			m_applyAdvect.SetTexture((int)ADVECTION.MACCORMACK, "_Write1f", buffer[WRITE]);
			m_applyAdvect.SetTexture((int)ADVECTION.MACCORMACK, "_Phi_n_1_hat", m_phi[PHI_N_1_HAT]);
			m_applyAdvect.SetTexture((int)ADVECTION.MACCORMACK, "_Phi_n_hat", m_phi[PHI_N_HAT]);
			m_applyAdvect.SetTexture((int)ADVECTION.MACCORMACK, "_Velocity", m_velocity[READ]);
			m_applyAdvect.SetTexture((int)ADVECTION.MACCORMACK, "_Obstacles", m_obstacles);
			
			m_applyAdvect.Dispatch((int)ADVECTION.MACCORMACK, (int)m_size.x/NUM_THREADS, (int)m_size.y/NUM_THREADS, (int)m_size.z/NUM_THREADS);
			
			Swap(buffer);
		}
		
		void ApplyAdvectionVelocity(float dt)
		{
			m_applyAdvect.SetVector("_Size", m_size);
			m_applyAdvect.SetFloat("_DeltaTime", dt);
			m_applyAdvect.SetFloat("_Dissipate", m_velocityDissipation);
			m_applyAdvect.SetFloat("_Forward", 1.0f);
			m_applyAdvect.SetFloat("_Decay", 0.0f);
			
			m_applyAdvect.SetTexture(0, "_Read3f", m_velocity[READ]);
			m_applyAdvect.SetTexture(0, "_Write3f", m_velocity[WRITE]);
			m_applyAdvect.SetTexture(0, "_Velocity", m_velocity[READ]);
			m_applyAdvect.SetTexture(0, "_Obstacles", m_obstacles);
			
			m_applyAdvect.Dispatch(0, (int)m_size.x/NUM_THREADS, (int)m_size.y/NUM_THREADS, (int)m_size.z/NUM_THREADS);
			
			Swap(m_velocity);
		}
		
		void ComputeVorticityConfinement(float dt)
		{
			m_computeVorticity.SetVector("_Size", m_size);
			
			m_computeVorticity.SetTexture(0, "_Write", m_temp3f);
			m_computeVorticity.SetTexture(0, "_Velocity", m_velocity[READ]);
			
			m_computeVorticity.Dispatch(0, (int)m_size.x/NUM_THREADS, (int)m_size.y/NUM_THREADS, (int)m_size.z/NUM_THREADS);
			
			m_computeConfinement.SetVector("_Size", m_size);
			m_computeConfinement.SetFloat("_DeltaTime", dt);
			m_computeConfinement.SetFloat("_Epsilon", m_vorticityStrength);
			
			m_computeConfinement.SetTexture(0, "_Write", m_velocity[WRITE]);
			m_computeConfinement.SetTexture(0, "_Read", m_velocity[READ]);
			m_computeConfinement.SetTexture(0, "_Vorticity", m_temp3f);
			
			m_computeConfinement.Dispatch(0, (int)m_size.x/NUM_THREADS, (int)m_size.y/NUM_THREADS, (int)m_size.z/NUM_THREADS);
			
			Swap(m_velocity);
		}
		
		void ComputeDivergence()
		{
			m_computeDivergence.SetVector("_Size", m_size);
			
			m_computeDivergence.SetTexture(0, "_Write", m_temp3f);
			m_computeDivergence.SetTexture(0, "_Velocity", m_velocity[READ]);
			m_computeDivergence.SetTexture(0, "_Obstacles", m_obstacles);
			
			m_computeDivergence.Dispatch(0, (int)m_size.x/NUM_THREADS, (int)m_size.y/NUM_THREADS, (int)m_size.z/NUM_THREADS);
		}
		
		void ComputePressure()
		{
			m_computeJacobi.SetVector("_Size", m_size);
			m_computeJacobi.SetTexture(0, "_Divergence", m_temp3f);
			m_computeJacobi.SetTexture(0, "_Obstacles", m_obstacles);
			
			for(int i = 0; i < m_iterations; i++)
			{
				m_computeJacobi.SetTexture(0, "_Write", m_pressure[WRITE]);
				m_computeJacobi.SetTexture(0, "_Pressure", m_pressure[READ]);
				
				m_computeJacobi.Dispatch(0, (int)m_size.x/NUM_THREADS, (int)m_size.y/NUM_THREADS, (int)m_size.z/NUM_THREADS);
				
				Swap(m_pressure);
			}
		}
		
		void ComputeProjection()
		{
			m_computeProjection.SetVector("_Size", m_size);
			m_computeProjection.SetTexture(0, "_Obstacles", m_obstacles);
			
			m_computeProjection.SetTexture(0, "_Pressure", m_pressure[READ]);
			m_computeProjection.SetTexture(0, "_Velocity", m_velocity[READ]);
			m_computeProjection.SetTexture(0, "_Write", m_velocity[WRITE]);
			
			m_computeProjection.Dispatch(0, (int)m_size.x/NUM_THREADS, (int)m_size.y/NUM_THREADS, (int)m_size.z/NUM_THREADS);
			
			Swap(m_velocity);
		}
		
		void Update () 
		{
			
			float dt = TIME_STEP;
			
			//First off advect any buffers that contain physical quantities like density or temperature by the 
			//velocity field. Advection is what moves values around.
			ApplyAdvection(dt, m_temperatureDissipation, 0.0f, m_temperature);
			
			//Normal advection can cause smoothing of the advected field making the results look less interesting.
			//BFECC is a method of advection that helps to prevents this smoothing at a extra performance cost but is less numerically stable.
			//MacCormack does the same as BFECC but is more (not completely) numerically stable and is more costly
			//You only really need to do this type of advection on visible fields, but you can do it on non visible ones if you want
			if(m_denstyAdvectionType == ADVECTION.BFECC)
			{
				ApplyAdvection(dt, 1.0f, 0.0f, m_density, 1.0f); //advect forward into write buffer
				ApplyAdvection(dt, 1.0f, 0.0f, m_density[READ], m_phi[PHI_N_HAT], -1.0f); //advect back into phi_n_hat buffer
				ApplyAdvectionBFECC(dt, m_densityDissipation, 0.0f, m_density); //advect using BFECC
			}
			else if(m_denstyAdvectionType == ADVECTION.MACCORMACK)
			{
				ApplyAdvection(dt, 1.0f, 0.0f, m_density[READ], m_phi[PHI_N_1_HAT], 1.0f); //advect forward into phi_n_1_hat buffer
				ApplyAdvection(dt, 1.0f, 0.0f, m_phi[PHI_N_1_HAT], m_phi[PHI_N_HAT], -1.0f); //advect back into phi_n_hat buffer
				ApplyAdvectionMacCormack(dt, m_densityDissipation, 0.0f, m_density);	
			}
			else
			{
				ApplyAdvection(dt, m_densityDissipation, 0.0f, m_density);
			}
			
			//The reaction advection looks better using normal for shorter lived fire (ie a hight decay rate) and 
			//looks better with BFECC or macCormack for longer lived fire (in my opinion)
			if(m_reactionAdvectionType == ADVECTION.BFECC)
			{
				ApplyAdvection(dt, 1.0f, 0.0f, m_reaction, 1.0f); //advect forward into write buffer
				ApplyAdvection(dt, 1.0f, 0.0f, m_reaction[READ], m_phi[PHI_N_HAT], -1.0f); //advect back into phi_n_hat buffer
				ApplyAdvectionBFECC(dt, 1.0f, m_reactionDecay, m_reaction); //advect using BFECC
			}
			else if(m_reactionAdvectionType == ADVECTION.MACCORMACK)
			{
				ApplyAdvection(dt, 1.0f, 0.0f, m_reaction[READ], m_phi[PHI_N_1_HAT], 1.0f); //advect forward into phi_n_1_hat buffer
				ApplyAdvection(dt, 1.0f, 0.0f, m_phi[PHI_N_1_HAT], m_phi[PHI_N_HAT], -1.0f); //advect back into phi_n_hat buffer
				ApplyAdvectionMacCormack(dt, 1.0f, m_reactionDecay, m_reaction);	
			}
			else
			{
				ApplyAdvection(dt, 1.0f, m_reactionDecay, m_reaction);
			}
			
			//The velocity field also advects its self. 
			ApplyAdvectionVelocity(dt);
			
			//Apply the effect the sinking colder smoke has on the velocity field
			ApplyBuoyancy(dt);
			
			//Adds a certain amount of reaction (fire) and temperate
			ApplyImpulse(dt, m_reactionAmount, m_reaction);
			ApplyImpulse(dt,  m_temperatureAmount, m_temperature);
			
			//The smoke is formed when the reaction is extinguished. When the reaction amount
			//falls below the extinguishment factor smoke is added
			ApplyExtinguishmentImpulse(dt, m_densityAmount, m_density);
			
			//The fuild sim math tends to remove the swirling movement of fluids.
			//This step will try and add it back in
			ComputeVorticityConfinement(dt);
			
			//Compute the divergence of the velocity field. In fluid simulation the
			//fluid is modelled as being incompressible meaning that the volume of the fluid
			//does not change over time. The divergence is the amount the field has deviated from being divergence free
			ComputeDivergence();
			
			//This computes the pressure need return the fluid to a divergence free condition
			ComputePressure();
			
			//Subtract the pressure field from the velocity field enforcing the divergence free conditions
			ComputeProjection();
			
			//rotation of box not support because ray cast in shader uses a AABB intersection
			transform.rotation = Quaternion.identity;
			
			GetComponent<Renderer>().material.SetVector("_Translate", transform.localPosition);
			GetComponent<Renderer>().material.SetVector("_Scale", transform.localScale);
			GetComponent<Renderer>().material.SetTexture("_Density", m_density[READ]);
            GetComponent<Renderer>().material.SetTexture("_Reaction", m_reaction[READ]);
		
		}
		
		void OnDestroy()
		{
			Destroy(m_density[READ]);	
			Destroy(m_density[WRITE]);
			Destroy(m_temperature[READ]);
			Destroy(m_temperature[WRITE]);
			Destroy(m_reaction[READ]);
			Destroy(m_reaction[WRITE]);
			Destroy(m_phi[PHI_N_1_HAT]);	
			Destroy(m_phi[PHI_N_HAT]);
			Destroy(m_velocity[READ]);
			Destroy(m_velocity[WRITE]);
			Destroy(m_pressure[READ]);
			Destroy(m_pressure[WRITE]);
			Destroy(m_obstacles);
			Destroy(m_temp3f);
					
		}
	}

}
	
	
	
	
	
	
	
	
	
	
	
	
	
	
	
	
	
	
	
	
	
	
	
	
	
	
	
	
	
	
