using System;
using System.Collections.Generic;
using UnityEngine;
using System.Linq;
using Barracuda;
using MLAgents.InferenceBrain;
using UnityEngine.Profiling;

namespace MLAgents
{
    public enum InferenceDevice
    {
        Cpu = 0,
        Gpu = 1
    }

    /// <summary>
    /// The Learning Brain works differently if you are training it or not.
    /// When training your Agents, drag the Learning Brain to the Academy's BroadcastHub and check
    /// the checkbox Control. When using a pre-trained model, just drag the Model file into the
    /// Model property of the Learning Brain.
    /// The property model corresponds to the Model currently attached to the Brain. Before
    /// being used, a call to ReloadModel is required.
    /// When the Learning Brain is not training, it uses a TensorFlow model to make decisions.
    /// The Proximal Policy Optimization (PPO) and Behavioral Cloning algorithms included with
    /// the ML-Agents SDK produce trained TensorFlow models that you can use with the
    /// Learning Brain.
    /// </summary>
    [CreateAssetMenu(fileName = "NewLearningBrain", menuName = "ML-Agents/Learning Brain")]
    public class LearningBrain : Brain
    {
        private ITensorAllocator m_TensorAllocator;
        private TensorGenerator m_TensorGenerator;
        private TensorApplier m_TensorApplier;
#if ENABLE_TENSORFLOW
        public TextAsset model;
        private ModelParamLoader _modelParamLoader;
        private TFSharpInferenceEngine _engine;
#else
        public NNModel model;
        private Model m_BarracudaModel;
        private IWorker m_Engine;
        private bool _verbose = false;

        private BarracudaModelParamLoader m_ModelParamLoader;
        private string[] m_OutputNames;
#endif

        [Tooltip("Inference execution device. CPU is the fastest option for most of ML Agents models. " +
            "(This field is not applicable for training).")]
        public InferenceDevice inferenceDevice = InferenceDevice.Cpu;

        private IReadOnlyList<TensorProxy> m_InferenceInputs;
        private IReadOnlyList<TensorProxy> m_InferenceOutputs;

        [NonSerialized]
        private bool m_IsControlled;

        /// <summary>
        /// When Called, the brain will be controlled externally. It will not use the
        /// model to decide on actions.
        /// </summary>
        public void SetToControlledExternally()
        {
            m_IsControlled = true;
        }

        /// <inheritdoc />
        protected override void Initialize()
        {
            ReloadModel();
        }

        /// <summary>
        /// Initializes the Brain with the Model that it will use when selecting actions for
        /// the agents
        /// </summary>
        /// <param name="seed"> The seed that will be used to initialize the RandomNormal
        /// and Multinomial objects used when running inference.</param>
        /// <exception cref="UnityAgentsException">Throws an error when the model is null
        /// </exception>
        public void ReloadModel(int seed = 0)
        {
            if (m_TensorAllocator == null)
                m_TensorAllocator = new TensorCachingAllocator();

#if ENABLE_TENSORFLOW
            if (model != null)
            {
                _engine = new TFSharpInferenceEngine();
                _engine.PrepareModel(model.bytes);
            }
            else
            {
                _engine = null;
            }
            _modelParamLoader = ModelParamLoader.GetLoaderAndCheck(_engine, brainParameters);
            _inferenceInputs = _modelParamLoader.GetInputTensors();
            _inferenceOutputs = _modelParamLoader.GetOutputTensors();
            _tensorGenerator = new TensorGenerator(brainParameters, seed, _tensorAllocator);
            _tensorApplier = new TensorApplier(brainParameters, seed, _tensorAllocator);
#else
            if (model != null)
            {
                #if BARRACUDA_VERBOSE
                _verbose = true;
                #endif

                D.logEnabled = _verbose;

                // Cleanup previous instance
                m_Engine?.Dispose();

                m_BarracudaModel = ModelLoader.Load(model.Value);
                var executionDevice = inferenceDevice == InferenceDevice.Gpu
                    ? BarracudaWorkerFactory.Type.ComputePrecompiled
                    : BarracudaWorkerFactory.Type.CSharp;

                m_Engine = BarracudaWorkerFactory.CreateWorker(executionDevice, m_BarracudaModel, _verbose);
            }
            else
            {
                m_BarracudaModel = null;
                m_Engine = null;
            }

            m_ModelParamLoader = BarracudaModelParamLoader.GetLoaderAndCheck(m_Engine, m_BarracudaModel, brainParameters);
            m_InferenceInputs = m_ModelParamLoader.GetInputTensors();
            m_OutputNames = m_ModelParamLoader.GetOutputNames();
            m_TensorGenerator = new TensorGenerator(brainParameters, seed, m_TensorAllocator, m_BarracudaModel);
            m_TensorApplier = new TensorApplier(brainParameters, seed, m_TensorAllocator, m_BarracudaModel);
#endif
        }

        /// <summary>
        /// Return a list of failed checks corresponding to the failed compatibility checks
        /// between the Model and the BrainParameters. Note : This does not reload the model.
        /// If changes have been made to the BrainParameters or the Model, the model must be
        /// reloaded using GiveModel before trying to get the compatibility checks.
        /// </summary>
        /// <returns> The list of the failed compatibility checks between the Model and the
        /// Brain Parameters</returns>
        public IEnumerable<string> GetModelFailedChecks()
        {
#if ENABLE_TENSORFLOW
            return (_modelParamLoader != null) ? _modelParamLoader.GetChecks() : new List<string>();
#else
            return (m_ModelParamLoader != null) ? m_ModelParamLoader.GetChecks() : new List<string>();
#endif
        }

        /// <inheritdoc />
        protected override void DecideAction()
        {
            if (m_IsControlled)
            {
                m_AgentInfos.Clear();
                return;
            }
            var currentBatchSize = m_AgentInfos.Count();
            if (currentBatchSize == 0)
            {
                return;
            }

            Profiler.BeginSample("LearningBrain.DecideAction");

#if ENABLE_TENSORFLOW
            if (_engine == null)
            {
                Debug.LogError($"No model was present for the Brain {name}.");
                return;
            }
            // Prepare the input tensors to be feed into the engine
            _tensorGenerator.GenerateTensors(_inferenceInputs, currentBatchSize, agentInfos);

            // Prepare the output tensors to be feed into the engine
            _tensorGenerator.GenerateTensors(_inferenceOutputs, currentBatchSize, agentInfos);

            // Execute the Model
            Profiler.BeginSample($"MLAgents.{name}.ExecuteGraph");
            _engine.ExecuteGraph(_inferenceInputs, _inferenceOutputs);
            Profiler.EndSample();

            // Update the outputs
            _tensorApplier.ApplyTensors(_inferenceOutputs, agentInfos);
#else
            if (m_Engine == null)
            {
                Debug.LogError($"No model was present for the Brain {name}.");
                return;
            }

            Profiler.BeginSample($"MLAgents.{name}.GenerateTensors");
            // Prepare the input tensors to be feed into the engine
            m_TensorGenerator.GenerateTensors(m_InferenceInputs, currentBatchSize, m_AgentInfos);
            Profiler.EndSample();

            Profiler.BeginSample($"MLAgents.{name}.PrepareBarracudaInputs");
            var inputs = PrepareBarracudaInputs();
            Profiler.EndSample();

            // Execute the Model
            Profiler.BeginSample($"MLAgents.{name}.ExecuteGraph");
            m_Engine.Execute(inputs);
            Profiler.EndSample();

            Profiler.BeginSample($"MLAgents.{name}.FetchBarracudaOutputs");
            m_InferenceOutputs = FetchBarracudaOutputs(m_OutputNames);
            Profiler.EndSample();

            Profiler.BeginSample($"MLAgents.{name}.ApplyTensors");
            // Update the outputs
            m_TensorApplier.ApplyTensors(m_InferenceOutputs, m_AgentInfos);
            Profiler.EndSample();
#endif
            m_AgentInfos.Clear();
            Profiler.EndSample();
        }

#if !ENABLE_TENSORFLOW
        Dictionary<string, Tensor> PrepareBarracudaInputs()
        {
            var inputs = new Dictionary<string, Tensor>();
            foreach (var inp in m_InferenceInputs)
            {
                inputs[inp.name] = inp.data;
            }

            return inputs;
        }

        List<TensorProxy> FetchBarracudaOutputs(string[] names)
        {
            var outputs = new List<TensorProxy>();
            foreach (var n in names)
            {
                var output = m_Engine.Peek(n);
                outputs.Add(TensorUtils.TensorProxyFromBarracuda(output, n));
            }

            return outputs;
        }

#endif

        public void OnDisable()
        {
#if !ENABLE_TENSORFLOW
            m_Engine?.Dispose();
#endif
            m_TensorAllocator?.Reset(false);
        }
    }
}
