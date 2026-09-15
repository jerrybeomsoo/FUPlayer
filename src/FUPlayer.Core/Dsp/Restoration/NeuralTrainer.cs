namespace FUPlayer.Core.Dsp.Restoration;

/// <summary>
/// Fits a <see cref="NeuralRepairModel"/> by gradient descent.
///
/// Plain backpropagation with Adam and mini-batches. The network is a few tens of thousands of
/// numbers, so this runs on a processor and needs nothing installed. Held-out frames are kept back
/// from the start and never trained on, because a loss measured on the training set says only that
/// the network can memorise.
///
/// A batch is divided between threads, each with its own gradient buffer and its own scratch, and the
/// gradients are added together before a single step is taken. That is the same arithmetic as running
/// the samples one after another, up to the order the sums happen in, and on eight cores it turns an
/// hour into ten minutes.
/// </summary>
public sealed class NeuralTrainer
{
    private const float Beta1 = 0.9f;
    private const float Beta2 = 0.999f;
    private const float Epsilon = 1e-8f;

    private readonly int[] _layers;
    private readonly float[] _parameters;
    private readonly float[] _gradient;
    private readonly float[] _moment;
    private readonly float[] _velocity;
    private readonly int[] _weightAt;
    private readonly int[] _biasAt;
    private readonly bool[] _decays;
    private readonly Worker[] _workers;

    private int _step;

    /// <summary>One thread's gradient and scratch, so that no two threads write to the same array.</summary>
    private sealed class Worker
    {
        public Worker(int[] layers, int parameters)
        {
            Gradient = new float[parameters];
            Activations = [.. layers.Select(size => new float[size])];
            Deltas = [.. layers.Select(size => new float[size])];
        }

        public float[] Gradient { get; }

        public float[][] Activations { get; }

        public float[][] Deltas { get; }

        public double Loss { get; set; }
    }

    public NeuralTrainer(int[] layers, int seed = 20260915)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(layers.Length, 2);
        _layers = layers;

        int count = NeuralRepairModel.ParameterCount(layers);
        _parameters = new float[count];
        _gradient = new float[count];
        _moment = new float[count];
        _velocity = new float[count];
        _workers = [.. Enumerable.Range(0, Math.Max(1, Environment.ProcessorCount)).Select(_ => new Worker(layers, count))];
        _weightAt = new int[layers.Length];
        _biasAt = new int[layers.Length];
        _decays = new bool[count];

        int at = 0;
        var random = new Random(seed);
        for (int layer = 1; layer < layers.Length; layer++)
        {
            _weightAt[layer] = at;
            int weights = layers[layer - 1] * layers[layer];

            // Xavier: keeps the signal's variance steady through a tanh stack.
            double spread = Math.Sqrt(6.0 / (layers[layer - 1] + layers[layer]));
            for (int i = 0; i < weights; i++)
            {
                _parameters[at + i] = (float)(((random.NextDouble() * 2.0) - 1.0) * spread);
            }

            // Only the weights are pulled towards zero. A bias carries the answer a unit gives when
            // it is told nothing, and the output biases are where a constant correction curve lives;
            // decaying those shrinks away the one part of the answer that generalises, and a heavily
            // decayed network then does worse than a lightly decayed one instead of better.
            for (int i = 0; i < weights; i++)
            {
                _decays[at + i] = true;
            }

            at += weights;
            _biasAt[layer] = at;
            at += layers[layer];
        }
    }

    public int ParameterCount => _parameters.Length;

    public float[] Parameters => _parameters;

    /// <summary>
    /// How hard the weights are pulled towards zero at every step. Without it a network of this size
    /// fits the releases it was trained on and does nothing for any others.
    /// </summary>
    public float WeightDecay { get; set; }

    /// <summary>One pass over a batch: accumulate gradients, then step. Returns the batch's mean squared error.</summary>
    public double Train(float[] inputs, float[] targets, int count, float learningRate)
    {
        int inputSize = _layers[0];
        int outputSize = _layers[^1];
        int threads = Math.Clamp(count / 16, 1, _workers.Length);

        Split(threads, count, (worker, from, to) =>
        {
            Array.Clear(worker.Gradient);
            worker.Loss = 0.0;
            for (int sample = from; sample < to; sample++)
            {
                Forward(worker, inputs.AsSpan(sample * inputSize, inputSize));
                worker.Loss += Backward(worker, targets.AsSpan(sample * outputSize, outputSize));
            }
        });

        Array.Clear(_gradient);
        double total = 0.0;
        for (int t = 0; t < threads; t++)
        {
            float[] gradient = _workers[t].Gradient;
            for (int i = 0; i < _gradient.Length; i++)
            {
                _gradient[i] += gradient[i];
            }

            total += _workers[t].Loss;
        }

        float scale = 1.0f / count;
        for (int i = 0; i < _gradient.Length; i++)
        {
            _gradient[i] *= scale;
        }

        Step(learningRate);
        return total / count;
    }

    /// <summary>Mean squared error over a batch, without changing anything.</summary>
    public double Evaluate(float[] inputs, float[] targets, int count)
    {
        int inputSize = _layers[0];
        int outputSize = _layers[^1];
        int threads = Math.Clamp(count / 16, 1, _workers.Length);

        Split(threads, count, (worker, from, to) =>
        {
            worker.Loss = 0.0;
            for (int sample = from; sample < to; sample++)
            {
                Forward(worker, inputs.AsSpan(sample * inputSize, inputSize));
                float[] output = worker.Activations[^1];
                for (int i = 0; i < outputSize; i++)
                {
                    double error = output[i] - targets[(sample * outputSize) + i];
                    worker.Loss += error * error;
                }
            }
        });

        double total = 0.0;
        for (int t = 0; t < threads; t++)
        {
            total += _workers[t].Loss;
        }

        return total / (count * outputSize);
    }

    /// <summary>Hands each worker its own run of samples. A single thread runs inline rather than scheduling.</summary>
    private void Split(int threads, int count, Action<Worker, int, int> work)
    {
        if (threads <= 1)
        {
            work(_workers[0], 0, count);
            return;
        }

        int each = (count + threads - 1) / threads;
        Parallel.For(0, threads, t =>
        {
            int from = t * each;
            int to = Math.Min(count, from + each);
            if (from < to)
            {
                work(_workers[t], from, to);
                return;
            }

            // Nothing to do, but its buffers still take part in the sum that follows.
            _workers[t].Loss = 0.0;
            Array.Clear(_workers[t].Gradient);
        });
    }

    private void Forward(Worker worker, ReadOnlySpan<float> input)
    {
        float[][] activations = worker.Activations;
        input.CopyTo(activations[0]);

        for (int layer = 1; layer < _layers.Length; layer++)
        {
            float[] previous = activations[layer - 1];
            float[] current = activations[layer];
            bool last = layer == _layers.Length - 1;
            int inputs = previous.Length;
            int weightAt = _weightAt[layer];
            int biasAt = _biasAt[layer];

            for (int unit = 0; unit < current.Length; unit++)
            {
                float sum = _parameters[biasAt + unit];
                int at = weightAt + (unit * inputs);
                for (int i = 0; i < inputs; i++)
                {
                    sum += _parameters[at + i] * previous[i];
                }

                current[unit] = last ? sum : MathF.Tanh(sum);
            }
        }
    }

    private double Backward(Worker worker, ReadOnlySpan<float> target)
    {
        float[][] activations = worker.Activations;
        float[][] deltas = worker.Deltas;
        float[] gradient = worker.Gradient;
        int last = _layers.Length - 1;
        float[] output = activations[last];
        float[] delta = deltas[last];
        double loss = 0.0;

        // The output layer is linear, so its delta is just the error.
        for (int i = 0; i < output.Length; i++)
        {
            float error = output[i] - target[i];
            delta[i] = error;
            loss += error * error;
        }

        for (int layer = last; layer >= 1; layer--)
        {
            float[] previous = activations[layer - 1];
            float[] current = deltas[layer];
            int inputs = previous.Length;
            int weightAt = _weightAt[layer];
            int biasAt = _biasAt[layer];

            for (int unit = 0; unit < current.Length; unit++)
            {
                float d = current[unit];
                if (d == 0.0f)
                {
                    continue;
                }

                gradient[biasAt + unit] += d;
                int at = weightAt + (unit * inputs);
                for (int i = 0; i < inputs; i++)
                {
                    gradient[at + i] += d * previous[i];
                }
            }

            if (layer == 1)
            {
                break;
            }

            // Push the error back, through the derivative of tanh, which is 1 - y².
            float[] below = deltas[layer - 1];
            Array.Clear(below);

            for (int unit = 0; unit < current.Length; unit++)
            {
                float d = current[unit];
                if (d == 0.0f)
                {
                    continue;
                }

                int at = weightAt + (unit * inputs);
                for (int i = 0; i < inputs; i++)
                {
                    below[i] += d * _parameters[at + i];
                }
            }

            for (int i = 0; i < below.Length; i++)
            {
                float y = previous[i];
                below[i] *= 1.0f - (y * y);
            }
        }

        return loss / output.Length;
    }

    private void Step(float learningRate)
    {
        _step++;
        float correction1 = 1.0f - MathF.Pow(Beta1, _step);
        float correction2 = 1.0f - MathF.Pow(Beta2, _step);

        for (int i = 0; i < _parameters.Length; i++)
        {
            float g = _gradient[i];
            _moment[i] = (Beta1 * _moment[i]) + ((1.0f - Beta1) * g);
            _velocity[i] = (Beta2 * _velocity[i]) + ((1.0f - Beta2) * g * g);

            float m = _moment[i] / correction1;
            float v = _velocity[i] / correction2;

            // Decoupled decay: applied to the weight rather than folded into the gradient, so the
            // adaptive step does not scale it away where the gradient is small.
            float decay = _decays[i] ? WeightDecay * _parameters[i] : 0.0f;
            _parameters[i] -= learningRate * ((m / (MathF.Sqrt(v) + Epsilon)) + decay);
        }
    }
}
