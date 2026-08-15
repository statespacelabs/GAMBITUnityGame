using System;
using Unity.Barracuda;

/// <summary>Fail-fast validation for the two bundled ONNX model interfaces.</summary>
public static class OnnxModelContractValidator
{
    public const int NavigatorHiddenSize = 128;
    public const int NavigatorActionSize = PolicyActionContract.ContinuousCount;
    public const int CombatHiddenSize = 512;

    public static void Validate(Model navigator, Model combat)
    {
        if (navigator == null)
            throw new ArgumentNullException(nameof(navigator));
        if (combat == null)
            throw new ArgumentNullException(nameof(combat));

        ValidateInput(navigator, "actor_observation", ActorObservationContract.Size);
        ValidateInput(navigator, "hidden_state", NavigatorHiddenSize);
        ValidateOutput(navigator, "action_mean");
        ValidateOutput(navigator, "next_hidden_state");

        ValidateInput(combat, "obs_norm", LocalObservationContract.Size);
        ValidateInput(combat, "hidden", CombatHiddenSize);
        ValidateOutput(combat, "action");
        ValidateOutput(combat, "next_hidden");
    }

    public static void ValidateRuntimeOutput(float[] values, int expectedSize, string outputName)
    {
        if (values == null || values.Length != expectedSize)
            throw new InvalidOperationException("ONNX output '" + outputName + "' expected "
                + expectedSize + " values but received " + (values == null ? 0 : values.Length));
        for (int index = 0; index < values.Length; index++)
            if (float.IsNaN(values[index]) || float.IsInfinity(values[index]))
                throw new InvalidOperationException("ONNX output '" + outputName
                    + "' is non-finite at index " + index);
    }

    private static void ValidateInput(Model model, string name, int expectedElements)
    {
        for (int index = 0; index < model.inputs.Count; index++)
        {
            Model.Input input = model.inputs[index];
            if (input.name != name)
                continue;
            int knownElements = 1;
            bool foundKnownDimension = false;
            if (input.shape != null)
            {
                for (int dimension = 0; dimension < input.shape.Length; dimension++)
                {
                    if (input.shape[dimension] <= 0)
                        continue;
                    knownElements *= input.shape[dimension];
                    foundKnownDimension = true;
                }
            }
            if (!foundKnownDimension || knownElements != expectedElements)
                throw new InvalidOperationException("ONNX input '" + name + "' expected "
                    + expectedElements + " known elements but model declares " + knownElements);
            return;
        }
        throw new InvalidOperationException("ONNX model is missing input '" + name + "'");
    }

    private static void ValidateOutput(Model model, string name)
    {
        for (int index = 0; index < model.outputs.Count; index++)
            if (model.outputs[index] == name)
                return;
        throw new InvalidOperationException("ONNX model is missing output '" + name + "'");
    }
}
