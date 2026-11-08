using System;

namespace Tickbird.Core.Data
{
    /// <summary>
    /// Represents a field key from a database measurement.
    /// </summary>
    public class FieldKey
    {
        #region Fields

        /// <summary>
        /// The name/key of the field.
        /// </summary>
        public readonly string Name;

        /// <summary>
        /// The field's type.
        /// </summary>
        public readonly string Type;

        #endregion Fields

        #region Constructors

        public FieldKey(string name, string type)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentNullException("name");
            if (string.IsNullOrWhiteSpace(type)) throw new ArgumentNullException("type");
            Name = name;
            Type = type;
        }

        #endregion Constructors

        #region Methods

        #endregion Methods
    }
}
